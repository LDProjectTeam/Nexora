using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Launcher self-update. Downloads a signed package, verifies its SHA-256 and the
    /// signature of the manifest, extracts only declared files and hands the swap to
    /// the standalone bootstrapper (a running EXE cannot replace itself safely).
    /// </summary>
    public class LauncherUpdateService : ILauncherUpdateService
    {
        private readonly HttpClient _http;
        private readonly IUpdateManifestProvider _provider;
        private readonly ISettingsService _settings;
        private readonly IUpdateStateStore _store;
        private readonly HttpDownloader? _fileDownloader;
        private readonly TimeSpan _readIdleTimeout;
        private readonly ILogger<LauncherUpdateService> _logger;

        private readonly object _sync = new();
        private LauncherUpdateManifest? _manifest;
        private string _stagingDir = string.Empty;
        private long _lastEmitTicks;

        private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

        public LauncherUpdateService(HttpClient http, IUpdateManifestProvider provider,
            ISettingsService settings, IUpdateStateStore store,
            ILogger<LauncherUpdateService> logger, HttpDownloader? fileDownloader = null,
            TimeSpan? readIdleTimeout = null)
        {
            _http = http;
            _provider = provider;
            _settings = settings;
            _store = store;
            _logger = logger;
            _fileDownloader = fileDownloader;
            _readIdleTimeout = readIdleTimeout ?? TimeSpan.FromSeconds(90);
        }

        public event EventHandler<LauncherUpdateStatus>? StatusChanged;

        public LauncherUpdateStatus Status { get; private set; } = new();

        public LauncherUpdateManifest? AvailableManifest => _manifest;

        public async Task<LauncherUpdateStatus> CheckForUpdatesAsync(string currentVersion,
            bool autoDownload = true, CancellationToken ct = default)
        {
            await _store.LoadAsync(ct).ConfigureAwait(false);
            _manifest = null;
            Status = new LauncherUpdateStatus
            {
                CurrentVersion = currentVersion,
                Phase = LauncherUpdatePhase.Checking
            };
            Emit(force: true);

            var url = _settings.Settings.LauncherUpdateManifestUrl?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    Phase = LauncherUpdatePhase.UpToDate
                }, force: true);
                return Status;
            }

            try
            {
                var manifest = await _provider.GetLauncherManifestAsync(url, ct).ConfigureAwait(false);
                _manifest = manifest;

                var state = _store.State.Launcher ??= new LauncherUpdatePersistedState();
                state.LastCheckedUtc = DateTimeOffset.UtcNow;

                if (!UpdateVersion.IsNewer(manifest.Version, currentVersion))
                {
                    state.ConsecutiveFailures = 0;
                    state.OfferedVersion = manifest.Version;
                    state.LastError = null;
                    state.DownloadReady = false;
                    await _store.SaveAsync(ct).ConfigureAwait(false);
                    SetStatus(new LauncherUpdateStatus
                    {
                        CurrentVersion = currentVersion,
                        AvailableVersion = manifest.Version,
                        Phase = LauncherUpdatePhase.UpToDate,
                        ReleaseNotes = manifest.ReleaseNotes
                    }, force: true);
                    return Status;
                }

                if (state.DownloadReady
                    && string.Equals(state.OfferedVersion, manifest.Version, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(state.StagingDir)
                    && Directory.Exists(state.StagingDir))
                {
                    _stagingDir = state.StagingDir;
                    SetStatus(new LauncherUpdateStatus
                    {
                        CurrentVersion = currentVersion,
                        AvailableVersion = manifest.Version,
                        Phase = LauncherUpdatePhase.ReadyToInstall,
                        TotalBytes = manifest.PackageSize,
                        DownloadedBytes = manifest.PackageSize,
                        StagingDir = _stagingDir,
                        ReleaseNotes = manifest.ReleaseNotes
                    }, force: true);
                    return Status;
                }

                if (state.OfferedVersion == manifest.Version && state.ConsecutiveFailures >= 2)
                {
                    SetStatus(new LauncherUpdateStatus
                    {
                        CurrentVersion = currentVersion,
                        AvailableVersion = manifest.Version,
                        Phase = LauncherUpdatePhase.Error,
                        ReleaseNotes = manifest.ReleaseNotes,
                        ErrorMessage = "The last launcher update attempts failed and were rolled back. " +
                                       "Automatic retries are paused to avoid an update loop."
                    }, force: true);
                    return Status;
                }

                state.OfferedVersion = manifest.Version;
                state.LastError = null;
                await _store.SaveAsync(ct).ConfigureAwait(false);

                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    AvailableVersion = manifest.Version,
                    Phase = LauncherUpdatePhase.UpdateAvailable,
                    TotalBytes = manifest.PackageSize,
                    ReleaseNotes = manifest.ReleaseNotes
                }, force: true);

                if (autoDownload && _settings.Settings.AutoDownloadLauncherUpdates)
                    _ = DownloadAsync(currentVersion, CancellationToken.None);

                return Status;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Launcher update check failed.");
                var state = _store.State.Launcher ??= new LauncherUpdatePersistedState();
                state.LastError = ex.Message;
                await _store.SaveAsync(ct).ConfigureAwait(false);
                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    Phase = LauncherUpdatePhase.Error,
                    ErrorMessage = ex.Message
                }, force: true);
                return Status;
            }
        }

        public async Task<OperationResult> DownloadAsync(string currentVersion, CancellationToken ct = default)
        {
            if (_manifest == null)
            {
                var checkedStatus = await CheckForUpdatesAsync(currentVersion, autoDownload: false, ct).ConfigureAwait(false);
                if (_manifest == null || checkedStatus.Phase != LauncherUpdatePhase.UpdateAvailable)
                    return OperationResult.Fail(checkedStatus.ErrorMessage ?? "No launcher update is available.");
            }

            var manifest = _manifest!;
            var versionDir = Path.Combine(PathHelper.LauncherUpdateDir, PathHelper.SanitizeFileName(manifest.Version));
            _stagingDir = Path.Combine(versionDir, "staging");
            Directory.CreateDirectory(versionDir);
            var packagePath = Path.Combine(versionDir, "package.zip");
            var partialPath = packagePath + ".part";

            try
            {
                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    AvailableVersion = manifest.Version,
                    Phase = LauncherUpdatePhase.Downloading,
                    TotalBytes = manifest.PackageSize,
                    ReleaseNotes = manifest.ReleaseNotes
                }, force: true);

                await DownloadPackageAsync(manifest, partialPath, ct).ConfigureAwait(false);
                if (!await VerifyFileAsync(partialPath, manifest.PackageSize, manifest.PackageSha256, ct)
                        .ConfigureAwait(false))
                {
                    TryDelete(partialPath);
                    throw new InvalidDataException("The launcher update package failed SHA-256 verification.");
                }
                File.Move(partialPath, packagePath, overwrite: true);

                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    AvailableVersion = manifest.Version,
                    Phase = LauncherUpdatePhase.Verifying,
                    TotalBytes = manifest.PackageSize,
                    DownloadedBytes = manifest.PackageSize,
                    ReleaseNotes = manifest.ReleaseNotes
                }, force: true);

                if (Directory.Exists(_stagingDir))
                    Directory.Delete(_stagingDir, true);
                Directory.CreateDirectory(_stagingDir);
                ExtractPackage(packagePath, _stagingDir, manifest);

                // Verify every extracted file against the signed manifest.
                foreach (var file in manifest.Files)
                {
                    if (!SafeRelativePath.TryResolve(_stagingDir, file.Path, out var staged)
                        || !await VerifyFileAsync(staged, file.Size, file.Sha256, ct).ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            $"The staged launcher file '{file.Path}' is missing or corrupted.");
                    }
                }

                var state = _store.State.Launcher ??= new LauncherUpdatePersistedState();
                state.DownloadReady = true;
                state.StagingDir = _stagingDir;
                state.LastError = null;
                await _store.SaveAsync(ct).ConfigureAwait(false);

                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    AvailableVersion = manifest.Version,
                    Phase = LauncherUpdatePhase.ReadyToInstall,
                    TotalBytes = manifest.PackageSize,
                    DownloadedBytes = manifest.PackageSize,
                    StagingDir = _stagingDir,
                    ReleaseNotes = manifest.ReleaseNotes
                }, force: true);
                return OperationResult.Ok(_stagingDir);
            }
            catch (OperationCanceledException)
            {
                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    AvailableVersion = manifest.Version,
                    Phase = LauncherUpdatePhase.UpdateAvailable,
                    TotalBytes = manifest.PackageSize,
                    ErrorMessage = "Launcher update download paused."
                }, force: true);
                return OperationResult.Cancelled("Launcher update download paused.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Launcher update download failed.");
                var state = _store.State.Launcher ??= new LauncherUpdatePersistedState();
                state.LastError = ex.Message;
                await _store.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(new LauncherUpdateStatus
                {
                    CurrentVersion = currentVersion,
                    AvailableVersion = manifest.Version,
                    Phase = LauncherUpdatePhase.Error,
                    TotalBytes = manifest.PackageSize,
                    ErrorMessage = ex.Message
                }, force: true);
                return OperationResult.Fail(ex.Message, ex);
            }
        }

        public async Task<OperationResult> DownloadBootstrapperAsync(BootstrapperUpdateInfo info,
            string destinationPath, IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Url)
                || info.Size <= 0 || string.IsNullOrWhiteSpace(info.Sha256))
                return OperationResult.Fail("The bootstrapper update manifest is incomplete.");

            if (_fileDownloader == null)
                return OperationResult.Fail("The bootstrapper downloader is not available.");

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var result = await _fileDownloader.DownloadFileAsync(
                info.Url, destinationPath, "vellum-bootstrapper",
                progress, _settings.Settings.MaxDownloadSpeedBps, ct).ConfigureAwait(false);
            if (!result.Success)
                return result;

            if (!await VerifyFileAsync(destinationPath, info.Size, info.Sha256, ct).ConfigureAwait(false))
            {
                TryDelete(destinationPath);
                return OperationResult.Fail("The bootstrapper update failed SHA-256 verification.");
            }

            return OperationResult.Ok(destinationPath);
        }

        public LauncherApplyRequest CreateApplyRequest(string installDir, string entryPoint,
            string backupDir, string resultFile, int waitProcessId, bool relaunch)
        {
            if (_manifest == null || string.IsNullOrWhiteSpace(_stagingDir))
                throw new InvalidOperationException("The launcher update has not been staged yet.");

            return new LauncherApplyRequest
            {
                WaitProcessId = waitProcessId,
                InstallDir = installDir,
                StagingDir = _stagingDir,
                BackupDir = backupDir,
                EntryPoint = entryPoint,
                Version = _manifest.Version,
                PreviousVersion = Status.CurrentVersion,
                Relaunch = relaunch,
                Files = _manifest.Files,
                DeletedFiles = _manifest.DeletedFiles,
                ResultFile = resultFile
            };
        }

        public async Task<OperationResult> PrepareRestartAsync(int currentProcessId, string currentVersion,
            CancellationToken ct = default)
        {
            if (_manifest == null || _store.State.Launcher?.DownloadReady != true)
                return OperationResult.Fail("The launcher update has not been downloaded yet.");

            var bootstrapper = Path.Combine(AppContext.BaseDirectory, "Bootstrapper", "Vellum.Bootstrapper.exe");
            if (!File.Exists(bootstrapper))
                bootstrapper = Path.Combine(AppContext.BaseDirectory, "Vellum.Bootstrapper.exe");
            if (!File.Exists(bootstrapper))
                return OperationResult.Fail(
                    "The launcher bootstrapper (Vellum.Bootstrapper.exe) is missing next to the launcher.");

            var manifest = _manifest!;
            var versionDir = Path.Combine(PathHelper.LauncherUpdateDir,
                PathHelper.SanitizeFileName(manifest.Version));
            var request = CreateApplyRequest(
                AppContext.BaseDirectory,
                manifest.EntryPoint,
                Path.Combine(versionDir, "backup"),
                PathHelper.LauncherUpdateResultFile,
                currentProcessId,
                relaunch: true);
            request.PreviousVersion = currentVersion;

            PathHelper.EnsureDirectory(PathHelper.LauncherUpdateDir);
            var requestPath = PathHelper.LauncherUpdateRequestFile;
            var temp = requestPath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(request, _json), ct).ConfigureAwait(false);
            File.Move(temp, requestPath, overwrite: true);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = bootstrapper,
                    Arguments = "--request \"" + requestPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (Exception ex)
            {
                return OperationResult.Fail("Could not start the launcher bootstrapper: " + ex.Message, ex);
            }

            SetStatus(new LauncherUpdateStatus
            {
                CurrentVersion = currentVersion,
                AvailableVersion = manifest.Version,
                Phase = LauncherUpdatePhase.Applying,
                TotalBytes = manifest.PackageSize,
                DownloadedBytes = manifest.PackageSize,
                StagingDir = _stagingDir
            }, force: true);
            return OperationResult.Ok();
        }

        public async Task<LauncherUpdateResultRecord?> ConsumeBootstrapperResultAsync(
            CancellationToken ct = default)
        {
            var path = PathHelper.LauncherUpdateResultFile;
            if (!File.Exists(path))
                return null;

            try
            {
                var record = JsonSerializer.Deserialize<LauncherUpdateResultRecord>(
                    await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), _json);
                TryDelete(path);
                await _store.LoadAsync(ct).ConfigureAwait(false);
                var state = _store.State.Launcher ??= new LauncherUpdatePersistedState();
                if (record != null)
                {
                    if (record.Success)
                    {
                        state.ConsecutiveFailures = 0;
                        state.LastError = null;
                        state.DownloadReady = false;
                        state.StagingDir = null;
                    }
                    else
                    {
                        state.ConsecutiveFailures++;
                        state.LastError = record.ErrorMessage;
                        state.DownloadReady = false;
                    }
                }
                await _store.SaveAsync(ct).ConfigureAwait(false);
                return record;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the launcher update result.");
                return null;
            }
        }

        private async Task DownloadPackageAsync(LauncherUpdateManifest manifest, string partialPath,
            CancellationToken ct)
        {
            long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (existing > manifest.PackageSize)
            {
                TryDelete(partialPath);
                existing = 0;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, manifest.PackageUrl);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                && existing == manifest.PackageSize)
                return;
            if (response.StatusCode == HttpStatusCode.PartialContent && existing > 0)
            {
            }
            else
            {
                response.EnsureSuccessStatusCode();
                existing = 0;
                TryDelete(partialPath);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(partialPath, existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous);

            var buffer = new byte[81920];
            long downloaded = existing;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long lastBytes = existing;
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            while (true)
            {
                idleCts.CancelAfter(_readIdleTimeout);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException(
                        $"The update server stopped sending data for {_readIdleTimeout.TotalSeconds:0} seconds.");
                }

                if (read <= 0)
                    break;
                await file.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                downloaded += read;

                var maxSpeed = _settings.Settings.MaxDownloadSpeedBps;
                if (maxSpeed > 0)
                {
                    var expectedSeconds = (double)(downloaded - existing) / maxSpeed;
                    var actualSeconds = clock.Elapsed.TotalSeconds;
                    if (expectedSeconds > actualSeconds)
                    {
                        var delay = TimeSpan.FromSeconds(expectedSeconds - actualSeconds);
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }

                var now = clock.Elapsed;
                if ((now - lastReport).TotalMilliseconds >= 350 || downloaded == manifest.PackageSize)
                {
                    var elapsed = (now - lastReport).TotalSeconds;
                    var speed = elapsed > 0 ? (long)((downloaded - lastBytes) / elapsed) : 0;
                    lastReport = now;
                    lastBytes = downloaded;
                    SetStatus(new LauncherUpdateStatus
                    {
                        CurrentVersion = Status.CurrentVersion,
                        AvailableVersion = manifest.Version,
                        Phase = LauncherUpdatePhase.Downloading,
                        DownloadedBytes = downloaded,
                        TotalBytes = manifest.PackageSize,
                        SpeedBytesPerSec = speed,
                        SecondsRemaining = speed > 0
                            ? (manifest.PackageSize - downloaded) / speed
                            : -1
                    });
                }
            }

            if (downloaded < manifest.PackageSize)
                throw new IOException(
                    $"The launcher package download closed early: {downloaded} of {manifest.PackageSize} bytes.");

            var max = _settings.Settings.MaxDownloadSpeedBps;
            if (max > 0 && clock.Elapsed.TotalSeconds > 0)
            {
                var expected = (double)manifest.PackageSize / max;
                if (expected > clock.Elapsed.TotalSeconds)
                    await Task.Delay(TimeSpan.FromSeconds(expected - clock.Elapsed.TotalSeconds), ct)
                        .ConfigureAwait(false);
            }
        }

        private static void ExtractPackage(string packagePath, string stagingDir,
            LauncherUpdateManifest manifest)
        {
            var allowed = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                var normalized = SafeRelativePath.NormalizeRelative(entry.FullName);
                if (normalized == null || !allowed.ContainsKey(normalized))
                    continue;
                if (!SafeRelativePath.TryResolve(stagingDir, normalized, out var destination))
                    throw new InvalidDataException($"Unsafe archive entry '{entry.FullName}'.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            foreach (var file in manifest.Files)
            {
                if (!SafeRelativePath.TryResolve(stagingDir, file.Path, out var destination)
                    || !File.Exists(destination))
                {
                    throw new InvalidDataException($"The package does not contain '{file.Path}'.");
                }
            }
        }

        private static async Task<bool> VerifyFileAsync(string path, long size, string sha256,
            CancellationToken ct)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size)
                return false;
            var actual = await HashHelper.ComputeSha256Async(path, ct).ConfigureAwait(false);
            return HashHelper.HashesMatch(actual, sha256);
        }

        private void SetStatus(LauncherUpdateStatus status, bool force = false)
        {
            lock (_sync)
                Status = status;
            Emit(force);
        }

        private void Emit(bool force)
        {
            if (!force)
            {
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastEmitTicks);
                if (now - last < 300)
                    return;
                Interlocked.Exchange(ref _lastEmitTicks, now);
            }
            StatusChanged?.Invoke(this, Status.Clone());
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
