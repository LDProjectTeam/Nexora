using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Applies a staged launcher package while the launcher process is not using its
    /// files. Used by the standalone bootstrapper and by tests. Only file copies and
    /// deletes are performed — the package can never execute a script.
    /// </summary>
    public class LauncherPackageInstaller
    {
        private readonly ILogger<LauncherPackageInstaller> _logger;

        private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

        /// <summary>Test seam: leaves an uncommitted journal behind after a failed apply.</summary>
        internal bool SuppressRollbackForTests { get; set; }

        public LauncherPackageInstaller(ILogger<LauncherPackageInstaller> logger)
        {
            _logger = logger;
        }

        public async Task<LauncherApplyResult> ApplyAsync(LauncherApplyRequest request,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var installDir = string.Empty;
            var backupDir = string.Empty;
            LauncherJournal? journal = null;

            try
            {
                ValidateRequest(request);
                installDir = Path.GetFullPath(request.InstallDir);
                backupDir = Path.GetFullPath(request.BackupDir);
                var stagingDir = Path.GetFullPath(request.StagingDir);

                if (!Directory.Exists(installDir))
                    return FailAndRecord(request, "The launcher install folder does not exist.");
                if (!Directory.Exists(stagingDir))
                    return FailAndRecord(request, "The staged launcher update is missing.");

                if (request.WaitProcessId > 0)
                    await WaitForProcessExitAsync(request.WaitProcessId, TimeSpan.FromSeconds(60), ct)
                        .ConfigureAwait(false);

                // 1. Payload must be intact before anything is replaced.
                var totalFiles = request.Files.Count;
                var checkedFiles = 0;
                foreach (var file in request.Files)
                {
                    if (!SafeRelativePath.TryResolve(stagingDir, file.Path, out var staged))
                        return FailAndRecord(request, $"Unsafe package path '{file.Path}'.");
                    if (!await VerifyFileAsync(staged, file.Size, file.Sha256, ct).ConfigureAwait(false))
                        return FailAndRecord(request, $"The staged file '{file.Path}' is missing or corrupted.");
                    checkedFiles++;
                    progress?.Report(new DownloadProgress
                    {
                        Fraction = totalFiles > 0 ? (double)checkedFiles / totalFiles : 1,
                        Phase = "Verifying",
                        PhaseKey = DownloadPhases.Verifying,
                        CurrentFile = file.Path
                    });
                }

                // 2. Prepare the journal and backups before the first change.
                Directory.CreateDirectory(backupDir);
                var installedStatePath = PathHelper.LauncherInstalledStateFile;
                var previousStateBackup = Path.Combine(backupDir, "installed-state.previous.json");
                var hadInstalledState = File.Exists(installedStatePath);
                if (hadInstalledState)
                    File.Copy(installedStatePath, previousStateBackup, overwrite: true);

                journal = new LauncherJournal
                {
                    InstallDir = installDir,
                    BackupDir = backupDir,
                    PreviousVersion = request.PreviousVersion,
                    NewVersion = request.Version,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    HadInstalledState = hadInstalledState
                };

                foreach (var file in request.Files)
                {
                    if (!SafeRelativePath.TryResolve(installDir, file.Path, out var target))
                        return FailAndRecord(request, $"Unsafe install path '{file.Path}'.");
                    var existed = File.Exists(target);
                    journal.Operations.Add(new LauncherOperation
                    {
                        Path = file.Path,
                        Write = true,
                        ExistedBefore = existed
                    });
                    if (existed)
                    {
                        if (!SafeRelativePath.TryResolve(backupDir, file.Path, out var backup))
                            return FailAndRecord(request, $"Unsafe backup path '{file.Path}'.");
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(target, backup, overwrite: true);
                    }
                }

                foreach (var deleted in request.DeletedFiles)
                {
                    if (!SafeRelativePath.TryResolve(installDir, deleted, out var target))
                        return FailAndRecord(request, $"Unsafe deleted path '{deleted}'.");
                    if (!File.Exists(target))
                        continue;
                    if (!SafeRelativePath.TryResolve(backupDir, deleted, out var backup))
                        return FailAndRecord(request, $"Unsafe backup path '{deleted}'.");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                    journal.Operations.Add(new LauncherOperation
                    {
                        Path = deleted,
                        Write = false,
                        ExistedBefore = true
                    });
                }

                await WriteJournalAsync(backupDir, journal, ct).ConfigureAwait(false);

                // 3. Replace files with temp + rename in the target directory.
                foreach (var file in request.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!SafeRelativePath.TryResolve(stagingDir, file.Path, out var staged)
                        || !SafeRelativePath.TryResolve(installDir, file.Path, out var target))
                    {
                        throw new InvalidDataException($"Unsafe package path '{file.Path}'.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temp = target + ".vellum-new-" + Guid.NewGuid().ToString("N");
                    File.Copy(staged, temp, overwrite: true);
                    if (File.Exists(target))
                    {
                        File.SetAttributes(target, FileAttributes.Normal);
                        File.Move(temp, target, overwrite: true);
                    }
                    else
                    {
                        File.Move(temp, target);
                    }
                }

                foreach (var deleted in request.DeletedFiles)
                {
                    if (SafeRelativePath.TryResolve(installDir, deleted, out var target) && File.Exists(target))
                    {
                        File.SetAttributes(target, FileAttributes.Normal);
                        File.Delete(target);
                    }
                }

                // 4. Verify the installed payload. Never report success otherwise.
                foreach (var file in request.Files)
                {
                    if (!SafeRelativePath.TryResolve(installDir, file.Path, out var installed)
                        || !await VerifyFileAsync(installed, file.Size, file.Sha256, ct).ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            $"The installed launcher file '{file.Path}' failed verification.");
                    }
                }

                TryDelete(Path.Combine(backupDir, "journal.json.new"));
                await WriteInstalledStateAsync(request, ct).ConfigureAwait(false);
                journal.Committed = true;
                await WriteJournalAsync(backupDir, journal, ct).ConfigureAwait(false);
                RecordResult(request, LauncherApplyResult.Ok(request.Version));
                _logger.LogInformation("Launcher update to {Version} applied from {Staging}.",
                    request.Version, stagingDir);
                return LauncherApplyResult.Ok(request.Version);
            }
            catch (OperationCanceledException)
            {
                var rollback = !SuppressRollbackForTests && journal != null
                    && await RestoreAsync(journal, ct).ConfigureAwait(false);
                if (rollback)
                    TryDelete(Path.Combine(backupDir, "journal.json"));
                return FailAndRecord(request, "The launcher update was cancelled.", rollback);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Launcher update failed.");
                var rollback = false;
                if (journal != null && !SuppressRollbackForTests)
                {
                    try { rollback = await RestoreAsync(journal, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception restoreEx) { _logger.LogError(restoreEx, "Launcher rollback failed."); }
                }
                if (rollback)
                    TryDelete(Path.Combine(backupDir, "journal.json"));
                return FailAndRecord(request,
                    "Launcher update failed: " + ex.Message, rollback);
            }
        }

        /// <summary>Restores the previous launcher files from a backup directory.</summary>
        public async Task<LauncherApplyResult> RollbackAsync(string installDir, string backupDir,
            CancellationToken ct = default)
        {
            var journalPath = Path.Combine(Path.GetFullPath(backupDir), "journal.json");
            if (!File.Exists(journalPath))
                return LauncherApplyResult.Fail("No launcher rollback journal was found.");

            try
            {
                var journal = JsonSerializer.Deserialize<LauncherJournal>(
                    await File.ReadAllTextAsync(journalPath, ct).ConfigureAwait(false), _json);
                if (journal == null)
                    return LauncherApplyResult.Fail("The launcher rollback journal is empty.");

                journal.InstallDir = installDir;
                journal.BackupDir = backupDir;
                var restored = await RestoreAsync(journal, ct).ConfigureAwait(false);
                if (restored)
                {
                    TryDelete(journalPath);
                    return LauncherApplyResult.Ok(journal.PreviousVersion);
                }
                return LauncherApplyResult.Fail("The launcher rollback did not complete.");
            }
            catch (Exception ex)
            {
                return LauncherApplyResult.Fail("Launcher rollback failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Checks for a launcher transaction that was interrupted before it committed.
        /// A committed journal is a normal manual-rollback backup and is left alone.
        /// </summary>
        public async Task<LauncherApplyResult?> RecoverInterruptedAsync(string installDir,
            CancellationToken ct = default, string? currentVersion = null)
        {
            if (!Directory.Exists(PathHelper.LauncherUpdateDir))
                return null;

            var journals = Directory.EnumerateFiles(
                    PathHelper.LauncherUpdateDir, "journal.json", SearchOption.AllDirectories)
                .Select(path => new { Path = path, Modified = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(item => item.Modified)
                .ToList();
            if (journals.Count == 0)
                return null;

            // Only the newest transaction can describe the current install. Older
            // journals are kept solely as manual rollback backups.
            var journalPath = journals[0].Path;
            try
            {
                var journal = JsonSerializer.Deserialize<LauncherJournal>(
                    await File.ReadAllTextAsync(journalPath, ct).ConfigureAwait(false), _json);
                if (journal == null || journal.Committed)
                    return null;

                // An external installer can replace Vellum while an old journal is
                // still present. Never roll the freshly installed build back to a
                // previous version that does not match the EXE we are about to start.
                if (!string.IsNullOrWhiteSpace(currentVersion)
                    && !UpdateVersion.AreSameVersion(currentVersion, journal.NewVersion)
                    && !UpdateVersion.AreSameVersion(currentVersion, journal.PreviousVersion))
                {
                    _logger.LogWarning(
                        "Ignoring launcher journal {Journal} for versions {New}/{Previous}; current EXE is {Current}.",
                        journalPath, journal.NewVersion, journal.PreviousVersion, currentVersion);
                    return null;
                }

                var backupDir = Path.GetDirectoryName(journalPath)!;
                _logger.LogWarning("Recovering interrupted launcher update in {Backup}.", backupDir);
                return await RollbackAsync(installDir, backupDir, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not recover launcher journal {Journal}.", journalPath);
            }
            return null;
        }

        public static async Task<LauncherInstalledState?> LoadInstalledStateAsync(
            CancellationToken ct = default)        {
            var path = PathHelper.LauncherInstalledStateFile;
            if (!File.Exists(path))
                return null;
            try
            {
                return JsonSerializer.Deserialize<LauncherInstalledState>(
                    await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), _json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the newest committed launcher backup that can safely restore the
        /// current install. Uncommitted journals are recovery data, not manual backups.
        /// </summary>
        public static string? FindLatestRollbackBackup(string? currentVersion = null)
        {
            if (!Directory.Exists(PathHelper.LauncherUpdateDir))
                return null;

            var journals = Directory.EnumerateFiles(
                    PathHelper.LauncherUpdateDir, "journal.json", SearchOption.AllDirectories)
                .Select(path => new { Path = path, Modified = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(item => item.Modified);
            foreach (var item in journals)
            {
                try
                {
                    var journal = JsonSerializer.Deserialize<LauncherJournal>(
                        File.ReadAllText(item.Path), _json);
                    if (journal == null || !journal.Committed)
                        continue;
                    if (!string.IsNullOrWhiteSpace(currentVersion)
                        && !UpdateVersion.AreSameVersion(currentVersion, journal.NewVersion)
                        && !UpdateVersion.AreSameVersion(currentVersion, journal.PreviousVersion))
                        continue;
                    return Path.GetDirectoryName(item.Path);
                }
                catch
                {
                    // Ignore an unreadable journal here; recovery reports it separately.
                }
            }
            return null;
        }

        private static async Task WriteInstalledStateAsync(LauncherApplyRequest request,
            CancellationToken ct)
        {
            PathHelper.EnsureDirectory(PathHelper.LauncherUpdateDir);
            var state = new LauncherInstalledState
            {
                Version = request.Version,
                InstalledUtc = DateTimeOffset.UtcNow,
                Files = request.Files.Select(file => new ManifestFile
                {
                    Path = file.Path,
                    Size = file.Size,
                    Sha256 = file.Sha256
                }).ToList(),
                DeletedFiles = request.DeletedFiles.ToList()
            };
            var path = PathHelper.LauncherInstalledStateFile;
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, _json), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }

        public static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout,
            CancellationToken ct = default)
        {
            if (processId <= 0)
                return;

            try
            {
                using var process = Process.GetProcessById(processId);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // The process already exited.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The launcher process {processId} did not exit within {timeout.TotalSeconds:0} seconds.");
            }
        }

        private async Task<bool> RestoreAsync(LauncherJournal journal, CancellationToken ct)
        {
            await Task.Yield();
            try
            {
                foreach (var operation in journal.Operations.AsEnumerable().Reverse())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!SafeRelativePath.TryResolve(journal.InstallDir, operation.Path, out var target))
                        continue;

                    if (operation.ExistedBefore)
                    {
                        if (!SafeRelativePath.TryResolve(journal.BackupDir, operation.Path, out var backup)
                            || !File.Exists(backup))
                        {
                            _logger.LogError("Launcher backup missing: {Path}", operation.Path);
                            return false;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (File.Exists(target))
                            File.SetAttributes(target, FileAttributes.Normal);
                        File.Copy(backup, target, overwrite: true);
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }

                var previousState = Path.Combine(journal.BackupDir, "installed-state.previous.json");
                if (File.Exists(previousState))
                {
                    PathHelper.EnsureDirectory(PathHelper.LauncherUpdateDir);
                    File.Copy(previousState, PathHelper.LauncherInstalledStateFile, overwrite: true);
                }
                else if (!journal.HadInstalledState && File.Exists(PathHelper.LauncherInstalledStateFile))
                {
                    File.Delete(PathHelper.LauncherInstalledStateFile);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restoring launcher files failed.");
                return false;
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

        private static void ValidateRequest(LauncherApplyRequest request)
        {
            if (request.SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported launcher update request schema {request.SchemaVersion}.");
            if (string.IsNullOrWhiteSpace(request.InstallDir))
                throw new InvalidDataException("The launcher install folder is missing.");
            if (string.IsNullOrWhiteSpace(request.StagingDir))
                throw new InvalidDataException("The launcher staging folder is missing.");
            if (string.IsNullOrWhiteSpace(request.BackupDir))
                throw new InvalidDataException("The launcher backup folder is missing.");
            if (request.Files.Count == 0)
                throw new InvalidDataException("The launcher update contains no files.");
            foreach (var file in request.Files)
            {
                var normalized = SafeRelativePath.RequireNormalized(file.Path, "launcher request");
                var replaced = normalized.Replace('\\', '/');
                if (replaced.StartsWith("Bootstrapper/", StringComparison.OrdinalIgnoreCase)
                    || replaced.Equals("Vellum.Bootstrapper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The launcher package cannot replace the running bootstrapper.");
                }
            }
            foreach (var deleted in request.DeletedFiles)
            {
                var normalized = SafeRelativePath.RequireNormalized(deleted, "launcher request");
                var replaced = normalized.Replace('\\', '/');
                if (replaced.StartsWith("Bootstrapper/", StringComparison.OrdinalIgnoreCase)
                    || replaced.Equals("Vellum.Bootstrapper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The launcher package cannot delete the running bootstrapper.");
                }
            }
            _ = SafeRelativePath.RequireNormalized(request.EntryPoint, "launcher request");
        }

        private static async Task WriteJournalAsync(string backupDir, LauncherJournal journal,
            CancellationToken ct)
        {
            var path = Path.Combine(backupDir, "journal.json");
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(journal, _json), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }

        private static void RecordResult(LauncherApplyRequest request, LauncherApplyResult result)
        {
            if (string.IsNullOrWhiteSpace(request.ResultFile))
                return;
            try
            {
                var directory = Path.GetDirectoryName(request.ResultFile);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                var record = new LauncherUpdateResultRecord
                {
                    Version = request.Version,
                    Success = result.Success,
                    RolledBack = result.RolledBack,
                    ErrorMessage = result.ErrorMessage
                };
                File.WriteAllText(request.ResultFile, JsonSerializer.Serialize(record, _json));
            }
            catch
            {
                // The result file is best effort; the bootstrapper still relaunches.
            }
        }

        private LauncherApplyResult FailAndRecord(LauncherApplyRequest request, string message,
            bool rolledBack = false)
        {
            _logger.LogError("Launcher update error: {Message}", message);
            var result = LauncherApplyResult.Fail(message, rolledBack);
            RecordResult(request, result);
            return result;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class LauncherOperation
        {
            public string Path { get; set; } = string.Empty;
            public bool Write { get; set; }
            public bool ExistedBefore { get; set; }
        }

        private sealed class LauncherJournal
        {
            public int SchemaVersion { get; set; } = 1;
            public string InstallDir { get; set; } = string.Empty;
            public string BackupDir { get; set; } = string.Empty;
            public string PreviousVersion { get; set; } = string.Empty;
            public string NewVersion { get; set; } = string.Empty;
            public DateTimeOffset CreatedUtc { get; set; }
            public bool Committed { get; set; }
            public bool HadInstalledState { get; set; }
            public List<LauncherOperation> Operations { get; set; } = new();
        }
    }
}
