using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;

namespace Vellum.Bootstrapper
{
    public partial class App : Application
    {
        private const string BootstrapperMutexName = @"Local\Vellum.Bootstrapper.SingleInstance";
        private const string LauncherMutexName = @"Local\Vellum.Launcher.SingleInstance";
        private const string SkipSelfUpdateArgument = "--skip-self-update";
        private const string SkipLauncherUpdateArgument = "--skip-launcher-update";

        private readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private SingleInstanceGuard? _guard;
        private HttpClient? _http;
        private SettingsService? _settings;
        private UpdateStateStore? _store;
        private LauncherUpdateService? _launcherUpdates;
        private LauncherPackageInstaller? _packageInstaller;
        private LauncherIntegrityService? _integrity;
        private BootstrapperWorkflow? _workflow;
        private BootstrapperSelfUpdater? _selfUpdater;
        private ProcessStarter? _processStarter;

        private string _appDirectory = AppContext.BaseDirectory;
        private string _launcherExecutable = "Vellum.exe";
        private string _currentBootstrapperVersion = "0.0.0";
        private List<string> _launcherArguments = new();
        private List<string> _originalBootstrapperArguments = new();
        private LauncherApplyRequest? _pendingApplyRequest;
        private string? _pendingApplyRequestPath;
        private bool _skipSelfUpdate;
        private bool _skipLauncherUpdate;
        private string? _healthFile;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                var args = e.Args.ToList();

                // A staged bootstrapper update runs without a window and without taking
                // the normal single-instance guard: it must wait for this old process.
                var applySelfIndex = args.FindIndex(a =>
                    string.Equals(a, "--apply-self", StringComparison.OrdinalIgnoreCase));
                if (applySelfIndex >= 0)
                {
                    var requestPath = applySelfIndex + 1 < args.Count ? args[applySelfIndex + 1] : null;
                    await RunSelfApplyAsync(requestPath).ConfigureAwait(false);
                    return;
                }

                _guard = SingleInstanceGuard.TryAcquire(BootstrapperMutexName);
                if (_guard == null || !_guard.Owned)
                {
                    _guard?.Dispose();
                    _guard = null;
                    Shutdown(0);
                    return;
                }

                ParseArguments(args);
                _appDirectory = ResolveAppDirectory();
                _currentBootstrapperVersion = ReadBootstrapperVersion();

                _settings = new SettingsService(NullLogger<SettingsService>.Instance);
                _settings.Load();
                var language = !string.IsNullOrWhiteSpace(_settings.Settings.Language)
                    ? _settings.Settings.Language
                    : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                BootstrapperText.SetLanguage(language);

                _http = new HttpClient(new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    MaxConnectionsPerServer = 8,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                })
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                var signature = new ManifestSignatureVerifier(_settings,
                    NullLogger<ManifestSignatureVerifier>.Instance);
                var provider = new HttpUpdateManifestProvider(_http, _settings, signature,
                    NullLogger<HttpUpdateManifestProvider>.Instance);
                _store = new UpdateStateStore(NullLogger<UpdateStateStore>.Instance);
                await _store.LoadAsync();

                var fileDownloader = new HttpDownloader(_http, NullLogger<HttpDownloader>.Instance);
                _launcherUpdates = new LauncherUpdateService(_http, provider, _settings, _store,
                    NullLogger<LauncherUpdateService>.Instance, fileDownloader);
                _packageInstaller = new LauncherPackageInstaller(
                    NullLogger<LauncherPackageInstaller>.Instance);
                _integrity = new LauncherIntegrityService(
                    NullLogger<LauncherIntegrityService>.Instance);
                _processStarter = new ProcessStarter();
                _workflow = new BootstrapperWorkflow(_launcherUpdates, _packageInstaller,
                    _integrity, _processStarter, NullLogger<BootstrapperWorkflow>.Instance);
                _selfUpdater = new BootstrapperSelfUpdater(_processStarter,
                    NullLogger<BootstrapperSelfUpdater>.Instance);

                _pendingApplyRequest = TryReadApplyRequest();

                var window = new BootstrapperWindow(RunStartupAsync, LaunchCurrentAsync, Shutdown);
                MainWindow = window;
                window.Show();

                if (!string.IsNullOrWhiteSpace(_healthFile))
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(_healthFile);
                        if (!string.IsNullOrWhiteSpace(directory))
                            Directory.CreateDirectory(directory);
                        File.WriteAllText(_healthFile, DateTimeOffset.UtcNow.ToString("O"));
                    }
                    catch
                    {
                        // The self-update helper will restore the previous bootstrapper.
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Vellum bootstrapper failed to start: " + ex.Message,
                    "Vellum", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private void ParseArguments(List<string> args)
        {
            _launcherArguments = new List<string>();
            _originalBootstrapperArguments = new List<string>();

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, SkipSelfUpdateArgument, StringComparison.OrdinalIgnoreCase))
                {
                    _skipSelfUpdate = true;
                    continue;
                }
                if (string.Equals(arg, SkipLauncherUpdateArgument, StringComparison.OrdinalIgnoreCase))
                {
                    _skipLauncherUpdate = true;
                    _originalBootstrapperArguments.Add(SkipLauncherUpdateArgument);
                    continue;
                }
                if (string.Equals(arg, "--request", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Count) i++;
                    _originalBootstrapperArguments.Add("--request");
                    if (i < args.Count) _originalBootstrapperArguments.Add(args[i]);
                    continue;
                }
                if (string.Equals(arg, "--wait-pid", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Count) i++;
                    continue;
                }
                if (string.Equals(arg, "--bootstrapper-health", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Count)
                    {
                        i++;
                        _healthFile = args[i];
                    }
                    continue;
                }
                if (string.Equals(arg, "--update-failed", StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith("--update-failed=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _launcherArguments.Add(arg);
                _originalBootstrapperArguments.Add(arg);
            }
        }

        private async Task<BootstrapperRunResult> RunStartupAsync(
            IProgress<BootstrapperProgress> progress, CancellationToken ct)
        {
            if (_launcherUpdates == null || _workflow == null || _processStarter == null)
                return BootstrapperRunResult.Failed("Bootstrapper services are not initialized.", false, false);

            BootstrapperLog.Write("RunStartup started.");
            var launcherRunning = SingleInstanceGuard.IsHeldByAnother(LauncherMutexName);
            BootstrapperLog.Write($"Launcher running: {launcherRunning}; pending apply: {_pendingApplyRequest != null}.");
            if (_pendingApplyRequest != null)
            {
                if (_pendingApplyRequest.WaitProcessId > 0)
                {
                    progress.Report(new BootstrapperProgress
                    {
                        Stage = BootstrapperStage.CheckingIntegrity,
                        Message = "Waiting for Vellum to close…"
                    });
                    try
                    {
                        await LauncherPackageInstaller.WaitForProcessExitAsync(
                            _pendingApplyRequest.WaitProcessId, TimeSpan.FromSeconds(60), ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return BootstrapperRunResult.Failed(
                            "Vellum did not close in time: " + ex.Message, true, false);
                    }
                    launcherRunning = SingleInstanceGuard.IsHeldByAnother(LauncherMutexName);
                }

                if (!launcherRunning)
                {
                    progress.Report(new BootstrapperProgress
                    {
                        Stage = BootstrapperStage.InstallingUpdate,
                        Message = "Installing the pending launcher update…"
                    });
                    var applyProgress = new Progress<DownloadProgress>(p =>
                        progress.Report(new BootstrapperProgress
                        {
                            Stage = BootstrapperStage.InstallingUpdate,
                            Message = "Installing the pending launcher update…",
                            Fraction = p.Fraction,
                            BytesDownloaded = p.BytesDownloaded,
                            TotalBytes = p.TotalBytes
                        }));
                    var apply = await _packageInstaller!.ApplyAsync(
                        _pendingApplyRequest, applyProgress, ct).ConfigureAwait(false);
                    TryDelete(_pendingApplyRequestPath);
                    _pendingApplyRequest = null;
                    _pendingApplyRequestPath = null;
                    if (!apply.Success)
                    {
                        BootstrapperLog.Write("Pending apply failed: " + apply.ErrorMessage);
                        var afterFailure = _integrity == null
                            ? null
                            : await _integrity.VerifyAsync(_appDirectory, _launcherExecutable, ct)
                                .ConfigureAwait(false);
                        BootstrapperLog.Write($"Pending apply failure integrity: {afterFailure?.Passed}.");
                        return BootstrapperRunResult.Failed(
                            "The pending launcher update failed: " + (apply.ErrorMessage ?? "unknown error"),
                            true, afterFailure?.Passed == true);
                    }
                }
            }

            // Bootstrapper updates happen before any launcher file is replaced. The new
            // executable continues in --apply-self mode after this process exits.
            if (!_skipSelfUpdate && !launcherRunning)
            {
                var selfUpdate = await TryStartBootstrapperUpdateAsync(progress, ct).ConfigureAwait(false);
                if (selfUpdate != null)
                    return selfUpdate;
            }

            var appVersion = ReadLauncherVersion(
                Path.Combine(_appDirectory, _launcherExecutable));
            var request = new BootstrapperRunRequest
            {
                AppDirectory = _appDirectory,
                LauncherExecutableName = _launcherExecutable,
                CurrentAppVersion = appVersion,
                LauncherArguments = _launcherArguments,
                LauncherIsRunning = launcherRunning,
                SkipLauncherUpdate = _skipLauncherUpdate,
                MarkBootstrappedLaunch = true
            };

            var result = await _workflow.RunAsync(request, progress, ct).ConfigureAwait(false);
            return result;
        }


        private async Task<BootstrapperRunResult?> TryStartBootstrapperUpdateAsync(
            IProgress<BootstrapperProgress> progress, CancellationToken ct)
        {
            if (_settings == null || _launcherUpdates == null || _store == null || _selfUpdater == null)
                return null;

            var launcherVersion = ReadLauncherVersion(Path.Combine(_appDirectory, _launcherExecutable));
            LauncherUpdateStatus status;
            try
            {
                progress.Report(new BootstrapperProgress
                {
                    Stage = BootstrapperStage.CheckingUpdates,
                    Message = BootstrapperText.Get("CheckingUpdates")
                });
                status = await _launcherUpdates.CheckForUpdatesAsync(
                    launcherVersion, autoDownload: false, ct).ConfigureAwait(false);
            }
            catch
            {
                return null; // A failed pre-check must never block the launcher.
            }

            var info = _launcherUpdates.AvailableManifest?.Bootstrapper;
            if (info == null || !UpdateVersion.IsNewer(info.Version, _currentBootstrapperVersion))
            {
                var state = _store.State.Launcher ??= new LauncherUpdatePersistedState();
                state.BootstrapperAttempts = 0;
                state.BootstrapperAttemptVersion = info?.Version;
                await _store.SaveAsync(ct).ConfigureAwait(false);
                return null;
            }

            var updateState = _store.State.Launcher ??= new LauncherUpdatePersistedState();
            var recentAttempt = updateState.BootstrapperAttemptUtc.HasValue
                && DateTimeOffset.UtcNow - updateState.BootstrapperAttemptUtc.Value < TimeSpan.FromMinutes(10);
            if (updateState.BootstrapperAttemptVersion == info.Version
                && updateState.BootstrapperAttempts >= 2 && recentAttempt)
            {
                return null;
            }

            var versionDir = Path.Combine(PathHelper.LauncherUpdateDir, "bootstrapper",
                PathHelper.SanitizeFileName(info.Version));
            var destination = Path.Combine(versionDir, "Vellum.Bootstrapper.exe");
            Directory.CreateDirectory(versionDir);

            progress.Report(new BootstrapperProgress
            {
                Stage = BootstrapperStage.UpdatingBootstrapper,
                Message = BootstrapperText.Get("UpdatingBootstrapper"),
                TotalBytes = info.Size
            });

            var downloadProgress = new Progress<DownloadProgress>(p =>
                progress.Report(new BootstrapperProgress
                {
                    Stage = BootstrapperStage.UpdatingBootstrapper,
                    Message = BootstrapperText.Get("UpdatingBootstrapper"),
                    Fraction = p.Fraction,
                    BytesDownloaded = p.BytesDownloaded,
                    TotalBytes = p.TotalBytes,
                    SpeedBytesPerSec = p.SpeedBytesPerSec,
                    SecondsRemaining = p.SecondsRemaining
                }));
            var download = await _launcherUpdates.DownloadBootstrapperAsync(
                info, destination, downloadProgress, ct).ConfigureAwait(false);
            if (!download.Success)
                return null; // Keep the current, known-working bootstrapper.

            updateState.BootstrapperAttemptVersion = info.Version;
            updateState.BootstrapperAttempts++;
            updateState.BootstrapperAttemptUtc = DateTimeOffset.UtcNow;
            await _store.SaveAsync(ct).ConfigureAwait(false);

            var request = new BootstrapperSelfUpdateRequest
            {
                TargetPath = Environment.ProcessPath ?? Path.Combine(
                    AppContext.BaseDirectory, "Vellum.Bootstrapper.exe"),
                NewExecutablePath = destination,
                BackupPath = Path.Combine(versionDir, "Vellum.Bootstrapper.exe.bak"),
                WaitProcessId = Environment.ProcessId,
                Version = info.Version,
                Size = info.Size,
                Sha256 = info.Sha256,
                OriginalArguments = _originalBootstrapperArguments,
                LauncherExecutablePath = Path.Combine(_appDirectory, _launcherExecutable),
                HealthFile = Path.Combine(versionDir, "health-" + Guid.NewGuid().ToString("N") + ".txt")
            };

            PathHelper.EnsureDirectory(PathHelper.LauncherUpdateDir);
            var requestPath = PathHelper.LauncherBootstrapperSelfUpdateRequestFile;
            WriteJson(requestPath, request);
            var start = _processStarter!.Start(destination,
                new[] { "--apply-self", requestPath }, versionDir);
            if (!start.Started)
            {
                return BootstrapperRunResult.Failed(
                    "The bootstrapper updater did not start: " + (start.ErrorMessage ?? "unknown error"),
                    true, true);
            }

            return new BootstrapperRunResult { Success = true, Launched = false, CanLaunchCurrent = true };
        }

        private async Task RunSelfApplyAsync(string? requestPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
                {
                    Environment.Exit(2);
                    return;
                }

                var request = JsonSerializer.Deserialize<BootstrapperSelfUpdateRequest>(
                    await File.ReadAllTextAsync(requestPath).ConfigureAwait(false), _json);
                if (request == null)
                {
                    Environment.Exit(2);
                    return;
                }

                _processStarter = new ProcessStarter();
                _selfUpdater = new BootstrapperSelfUpdater(_processStarter,
                    NullLogger<BootstrapperSelfUpdater>.Instance);
                var progress = new Progress<BootstrapperProgress>(_ => { });
                var result = await _selfUpdater.ApplyAsync(request, progress).ConfigureAwait(false);
                TryDelete(requestPath);
                Environment.Exit(result.Success || result.Launched ? 0 : 3);
            }
            catch
            {
                Environment.Exit(3);
            }
        }

        private Task<BootstrapperRunResult> LaunchCurrentAsync()
        {
            try
            {
                var exe = Path.Combine(_appDirectory, _launcherExecutable);
                var args = new List<string> { BootstrapperLaunchPolicy.ChildMarkerArgument };
                args.AddRange(_launcherArguments);
                var token = BootstrapperHandshake.CreateToken();
                var result = _processStarter!.Start(exe, args, _appDirectory, token);
                return Task.FromResult(result.Started
                    ? BootstrapperRunResult.Ok(true)
                    : BootstrapperRunResult.Failed(
                        "Could not start Vellum: " + (result.ErrorMessage ?? "unknown error"), true, true));
            }
            catch (Exception ex)
            {
                return Task.FromResult(BootstrapperRunResult.Failed(ex.Message, true, true));
            }
        }

        private LauncherApplyRequest? TryReadApplyRequest()
        {
            try
            {
                var requestPath = PathHelper.LauncherUpdateRequestFile;
                if (!File.Exists(requestPath))
                    return null;
                var request = JsonSerializer.Deserialize<LauncherApplyRequest>(
                    File.ReadAllText(requestPath), _json);
                if (request == null)
                    return null;

                try
                {
                    var installed = LauncherPackageInstaller.LoadInstalledStateAsync()
                        .GetAwaiter().GetResult();
                    if (installed != null
                        && UpdateVersion.AreSameVersion(installed.Version, request.Version))
                    {
                        TryDelete(requestPath);
                        return null;
                    }
                }
                catch
                {
                    // A missing/corrupt state file is handled by the normal recovery path.
                }

                _pendingApplyRequestPath = requestPath;
                return request;
            }
            catch
            {
                return null;
            }
        }

        private string ResolveAppDirectory()
        {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (File.Exists(Path.Combine(baseDir, _launcherExecutable)))
                return baseDir;

            var parent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent)
                && File.Exists(Path.Combine(parent, _launcherExecutable)))
            {
                return parent;
            }
            return parent ?? baseDir;
        }

        private static string ReadLauncherVersion(string executable)
        {
            try
            {
                return File.Exists(executable)
                    ? FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadBootstrapperVersion()
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "0.0.0";
                return typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            }
            catch
            {
                return typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            }
        }

        private void WriteJson<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, _json));
            File.Move(temp, path, overwrite: true);
        }

        private static void TryDelete(string? path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _http?.Dispose();
            _guard?.Dispose();
            _guard = null;
            base.OnExit(e);
        }
    }
}
