using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Applies a staged update transactionally:
    /// preflight → journal → backup replaced files → atomic file moves → integrity
    /// verification → commit → cleanup. Any failure restores the backup. A crash leaves
    /// a journal which is rolled back at the next launcher start.
    /// </summary>
    public class UpdateInstaller : IUpdateInstaller
    {
        private readonly IIntegrityVerifier _verifier;
        private readonly IUpdateStateStore _store;
        private readonly IDiskSpaceService _diskSpace;
        private readonly ILogger<UpdateInstaller> _logger;

        /// <summary>Test seam: called after each file operation is applied.</summary>
        internal Action<int, string>? OperationAppliedHook { get; set; }

        /// <summary>Test seam: leaves the journal behind when an apply fails.</summary>
        internal bool SuppressRollbackForTests { get; set; }

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public UpdateInstaller(IIntegrityVerifier verifier, IUpdateStateStore store,
            IDiskSpaceService diskSpace, ILogger<UpdateInstaller> logger)
        {
            _verifier = verifier;
            _store = store;
            _diskSpace = diskSpace;
            _logger = logger;
        }

        public async Task<OperationResult> ApplyAsync(
            GameUpdatePlan plan, string stagingRoot, UpdatePersistedState state,
            Func<Task>? beforeApply = null, IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var gameId = plan.GameId;
            var installRoot = Path.GetFullPath(plan.InstallRoot);
            var payloadRoot = Path.Combine(stagingRoot, "payload");
            var backupRoot = Path.Combine(PathHelper.UpdateRollbackDir, PathHelper.SanitizeFileName(gameId));

            try
            {
                Directory.CreateDirectory(installRoot);

                // 1. Every file we are about to install must be present and intact.
                var preflight = await VerifyStagedPayloadAsync(plan, payloadRoot, progress, ct)
                    .ConfigureAwait(false);
                if (!preflight.Success)
                    return preflight;

                // 2. Free space for the backup copy (downloads are already staged).
                var operations = BuildOperations(plan, installRoot, backupRoot);
                var backupBytes = operations.Where(o => o.ExistedBefore)
                    .Sum(o => Math.Max(0, o.OriginalSize));
                var free = _diskSpace.GetAvailableFreeSpace(installRoot, out var drive);
                if (backupBytes > 0 && free < backupBytes)
                {
                    return OperationResult.Fail(
                        $"Not enough free space on {drive} to back up the files being replaced. " +
                        $"Required: {PathHelper.FormatBytes(backupBytes)}, available: {PathHelper.FormatBytes(free)}.");
                }

                var backupFree = _diskSpace.GetAvailableFreeSpace(PathHelper.UpdateRollbackDir, out var backupDrive);
                if (backupBytes > 0 && backupFree < backupBytes
                    && !string.Equals(drive, backupDrive, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail(
                        $"Not enough free space on {backupDrive} for the rollback backup. " +
                        $"Required: {PathHelper.FormatBytes(backupBytes)}, available: {PathHelper.FormatBytes(backupFree)}.");
                }

                // 3. Windows: a running game leaves executables locked. Detect it before
                //    touching anything, so the installed build is never left half-updated.
                var lockError = ValidateTargetsWritable(operations, installRoot);
                if (lockError != null)
                    return OperationResult.Fail(lockError);

                if (beforeApply != null)
                    await beforeApply().ConfigureAwait(false);

                // 4. Write the journal before the first change.
                var journal = new InstallJournal
                {
                    GameId = gameId,
                    InstallRoot = installRoot,
                    StagingRoot = stagingRoot,
                    BackupRoot = backupRoot,
                    PreviousVersion = state.Games.TryGetValue(gameId, out var existingRecord)
                        ? existingRecord.CurrentVersion : string.Empty,
                    PreviousBuildId = existingRecord?.BuildId ?? string.Empty,
                    NewVersion = plan.Manifest.Version,
                    NewBuildId = plan.Manifest.BuildId,
                    Operations = operations,
                    PreviousFiles = plan.PreviousManifest.ToList(),
                    NewFiles = plan.Manifest.Files.Select(f => new InstalledFileRecord
                    {
                        Path = f.Path,
                        Size = f.Size,
                        Sha256 = f.Sha256
                    }).ToList(),
                    State = JournalState.Prepared,
                    StartedUtc = DateTimeOffset.UtcNow
                };
                PrepareBackupDirectory(gameId, backupRoot);
                await SaveJournalAsync(journal, ct).ConfigureAwait(false);

                journal.State = JournalState.Applying;
                await SaveJournalAsync(journal, ct).ConfigureAwait(false);

                var applied = 0;
                foreach (var operation in operations)
                {
                    ct.ThrowIfCancellationRequested();
                    journal.StartedOperations = applied + 1;
                    await SaveJournalAsync(journal, ct).ConfigureAwait(false);
                    await ApplyOperationAsync(operation, installRoot, payloadRoot, backupRoot, ct)
                        .ConfigureAwait(false);
                    applied++;
                    OperationAppliedHook?.Invoke(applied, operation.Path);
                    progress?.Report(new DownloadProgress
                    {
                        GameId = gameId,
                        Fraction = operations.Count > 0 ? (double)applied / operations.Count : 1,
                        Phase = Localizer.T("UpdatePhaseInstalling", "Installing update…"),
                        PhaseKey = DownloadPhases.Installing,
                        CurrentFile = operation.Path
                    });
                }

                // 5. Do not mark the update installed until the installed tree verifies.
                progress?.Report(new DownloadProgress
                {
                    GameId = gameId,
                    Fraction = 0,
                    Phase = Localizer.T("UpdatePhaseVerifying", "Verifying update…"),
                    PhaseKey = DownloadPhases.Verifying
                });
                var verification = await _verifier.VerifyRootAsync(
                    plan.Manifest, installRoot, requireAllFiles: true, progress, ct).ConfigureAwait(false);
                if (!verification.Passed)
                {
                    throw new InvalidDataException(
                        "The installed files failed integrity verification after applying the update.");
                }

                journal.State = JournalState.Committed;
                await SaveJournalAsync(journal, ct).ConfigureAwait(false);

                // 6. Persist the new version only after a successful verification.
                var record = GetOrCreateRecord(state, gameId);
                record.InstallPath = installRoot;
                record.CurrentVersion = plan.Manifest.Version;
                record.BuildId = plan.Manifest.BuildId;
                record.Channel = plan.Manifest.Channel;
                record.PendingManifestUrl = null;
                record.PendingManifestPath = null;
                record.PendingVersion = null;
                record.PendingBuildId = null;
                record.Phase = GameUpdatePhase.UpToDate;
                record.LastUpdatedUtc = DateTimeOffset.UtcNow;
                record.ErrorMessage = null;
                record.DownloadCompleted = false;
                record.DownloadedBytes = 0;
                record.TotalBytes = 0;
                record.UpdateSizeBytes = 0;
                record.ChangedFileCount = 0;
                record.AddedFileCount = 0;
                record.DeletedFileCount = 0;
                record.IsQueued = false;
                record.RollbackAvailable = true;
                record.InstalledFiles = plan.Manifest.Files.Select(f => new InstalledFileRecord
                {
                    Path = f.Path,
                    Size = f.Size,
                    Sha256 = f.Sha256
                }).ToList();

                await SaveRollbackRecordAsync(journal, ct).ConfigureAwait(false);
                await _store.SaveAsync(ct).ConfigureAwait(false);
                TryDeleteJournal(gameId);
                TryDeleteDirectory(payloadRoot);

                _logger.LogInformation("Applied update {Version} ({Build}) for {GameId}.",
                    plan.Manifest.Version, plan.Manifest.BuildId, gameId);
                return OperationResult.Ok(installRoot);
            }
            catch (OperationCanceledException)
            {
                var outcome = SuppressRollbackForTests
                    ? new RollbackOutcome { Success = false, ErrorMessage = "Rollback suppressed by test." }
                    : await TryRollbackJournalAsync(gameId, installRoot).ConfigureAwait(false);
                return OperationResult.Cancelled(outcome.Success
                    ? "Update paused; previous version restored."
                    : "Update paused.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update installation failed for {GameId}.", gameId);
                var outcome = SuppressRollbackForTests
                    ? new RollbackOutcome { Success = false, ErrorMessage = "Rollback suppressed by test." }
                    : await TryRollbackJournalAsync(gameId, installRoot).ConfigureAwait(false);
                var suffix = outcome.Success
                    ? " The previous version was restored."
                    : " Rollback also failed: " + (outcome.ErrorMessage ?? "unknown error");
                return OperationResult.Fail("Update installation failed: " + ex.Message + suffix, ex);
            }
        }

        public async Task<RollbackOutcome> RollbackAsync(string gameId, string installRoot,
            CancellationToken ct = default)
        {
            var rollbackPath = GetRollbackRecordPath(gameId);
            if (!File.Exists(rollbackPath))
                return new RollbackOutcome { Success = false, ErrorMessage = "No rollback data is available for this game." };

            InstallJournal? record;
            try
            {
                record = JsonSerializer.Deserialize<InstallJournal>(
                    await File.ReadAllTextAsync(rollbackPath, ct).ConfigureAwait(false), _json);
            }
            catch (Exception ex)
            {
                return new RollbackOutcome { Success = false, ErrorMessage = "Rollback data is corrupted: " + ex.Message };
            }
            if (record == null || record.Operations.Count == 0)
                return new RollbackOutcome { Success = false, ErrorMessage = "Rollback data is empty." };

            var outcome = await RestoreAsync(record, installRoot, ct).ConfigureAwait(false);
            if (!outcome.Success)
                return outcome;

            var state = _store.State;
            var entry = GetOrCreateRecord(state, gameId);
            entry.CurrentVersion = record.PreviousVersion;
            entry.BuildId = record.PreviousBuildId;
            entry.InstalledFiles = record.PreviousFiles.ToList();
            entry.Phase = GameUpdatePhase.UpdateAvailable;
            entry.RollbackAvailable = false;
            entry.LastUpdatedUtc = DateTimeOffset.UtcNow;
            entry.ErrorMessage = "Rolled back to the previous build.";
            await _store.SaveAsync(ct).ConfigureAwait(false);
            TryDelete(rollbackPath);

            outcome.RestoredVersion = record.PreviousVersion;
            return outcome;
        }

        public async Task<IReadOnlyList<string>> RecoverAsync(CancellationToken ct = default)
        {
            var recovered = new List<string>();
            PathHelper.EnsureDirectory(PathHelper.UpdateJournalsDir);
            foreach (var path in Directory.EnumerateFiles(PathHelper.UpdateJournalsDir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                InstallJournal? journal = null;
                try
                {
                    journal = JsonSerializer.Deserialize<InstallJournal>(
                        await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), _json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not read update journal {Path}; keeping it for inspection.", path);
                }

                if (journal == null)
                    continue;

                try
                {
                    if (journal.State != JournalState.Committed)
                    {
                        _logger.LogWarning("Recovering interrupted update for {GameId}.", journal.GameId);
                        var outcome = await RestoreAsync(journal, journal.InstallRoot, ct).ConfigureAwait(false);
                        if (!outcome.Success)
                        {
                            _logger.LogError("Rollback after interruption failed for {GameId}: {Error}",
                                journal.GameId, outcome.ErrorMessage);
                            continue;
                        }
                    }

                    if (journal.State == JournalState.Committed)
                        await SaveRollbackRecordAsync(journal, ct).ConfigureAwait(false);

                    if (_store.State.Games.TryGetValue(journal.GameId, out var record))
                    {
                        if (journal.State == JournalState.Committed)
                        {
                            record.Phase = GameUpdatePhase.UpToDate;
                            record.CurrentVersion = journal.NewVersion;
                            record.BuildId = journal.NewBuildId;
                            record.InstalledFiles = journal.NewFiles.ToList();
                            record.PendingManifestUrl = null;
                            record.PendingVersion = null;
                            record.PendingBuildId = null;
                            record.RollbackAvailable = true;
                            record.LastUpdatedUtc = DateTimeOffset.UtcNow;
                            record.ErrorMessage = null;
                        }
                        else
                        {
                            record.Phase = GameUpdatePhase.UpdateAvailable;
                            record.ErrorMessage = "The update was interrupted and the previous version was restored.";
                        }
                    }

                    TryDelete(path);
                    recovered.Add(journal.GameId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Update recovery failed for {GameId}.", journal.GameId);
                }
            }

            if (recovered.Count > 0)
                await _store.SaveAsync(ct).ConfigureAwait(false);
            return recovered;
        }

        internal async Task<RollbackOutcome> TryRollbackJournalAsync(string gameId, string installRoot)
        {
            var journalPath = GetJournalPath(gameId);
            if (!File.Exists(journalPath))
                return new RollbackOutcome { Success = false, ErrorMessage = "No journal." };

            try
            {
                var journal = JsonSerializer.Deserialize<InstallJournal>(
                    await File.ReadAllTextAsync(journalPath).ConfigureAwait(false), _json);
                if (journal == null)
                    return new RollbackOutcome { Success = false, ErrorMessage = "Empty journal." };

                var outcome = await RestoreAsync(journal, installRoot, CancellationToken.None).ConfigureAwait(false);
                if (outcome.Success)
                {
                    TryDelete(journalPath);
                    if (_store.State.Games.TryGetValue(gameId, out var record))
                    {
                        record.Phase = GameUpdatePhase.UpdateAvailable;
                        record.ErrorMessage = "The update failed and the previous version was restored.";
                    }
                }
                return outcome;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rollback failed for {GameId}.", gameId);
                return new RollbackOutcome { Success = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<RollbackOutcome> RestoreAsync(InstallJournal journal, string installRoot,
            CancellationToken ct)
        {
            await Task.Yield();
            try
            {
                var started = Math.Clamp(journal.StartedOperations, 0, journal.Operations.Count);
                foreach (var operation in journal.Operations.Take(started).AsEnumerable().Reverse())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!SafeRelativePath.TryResolve(installRoot, operation.Path, out var target))
                        continue;

                    if (operation.ExistedBefore)
                    {
                        if (!SafeRelativePath.TryResolve(journal.BackupRoot, operation.Path, out var backup)
                            || !File.Exists(backup))
                        {
                            // Backups are written before a file is modified, so a
                            // missing backup means this operation was never applied.
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (File.Exists(target))
                            File.SetAttributes(target, FileAttributes.Normal);
                        File.Copy(backup, target, overwrite: true);
                    }
                    else
                    {
                        if (File.Exists(target))
                            File.Delete(target);
                    }
                }

                TryDeleteDirectory(journal.BackupRoot);
                return new RollbackOutcome { Success = true, RestoredVersion = journal.PreviousVersion };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restoring the previous build failed.");
                return new RollbackOutcome { Success = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<OperationResult> VerifyStagedPayloadAsync(GameUpdatePlan plan, string payloadRoot,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var total = plan.Diff.Downloads.Count();
            var done = 0;
            foreach (var change in plan.Diff.Downloads)
            {
                if (!SafeRelativePath.TryResolve(payloadRoot, change.Path, out var staged))
                    return OperationResult.Fail($"Unsafe staged path '{change.Path}'.");
                if (!await _verifier.VerifyFileAsync(staged, change.Size, change.Sha256, ct).ConfigureAwait(false))
                    return OperationResult.Fail($"The staged file '{change.Path}' is missing or corrupted.");

                done++;
                progress?.Report(new DownloadProgress
                {
                    GameId = plan.GameId,
                    Fraction = total > 0 ? (double)done / total : 1,
                    Phase = Localizer.T("UpdatePhaseVerifying", "Verifying update…"),
                    PhaseKey = DownloadPhases.Verifying,
                    CurrentFile = change.Path
                });
            }
            return OperationResult.Ok();
        }

        private List<JournalOperation> BuildOperations(GameUpdatePlan plan, string installRoot, string backupRoot)
        {
            var operations = new List<JournalOperation>();
            foreach (var change in plan.Diff.Changes)
            {
                if (change.Kind is FileChangeKind.Unchanged)
                    continue;
                if (!SafeRelativePath.TryResolve(installRoot, change.Path, out var target))
                    throw new InvalidDataException($"Unsafe install path '{change.Path}'.");
                if (!SafeRelativePath.TryResolve(backupRoot, change.Path, out var backup))
                    throw new InvalidDataException($"Unsafe backup path '{change.Path}'.");

                var exists = File.Exists(target);
                operations.Add(new JournalOperation
                {
                    Path = change.Path,
                    Action = change.Kind == FileChangeKind.Deleted ? JournalAction.Delete : JournalAction.Write,
                    ExistedBefore = exists,
                    OriginalSize = exists ? new FileInfo(target).Length : 0,
                    BackupPath = backup
                });
            }
            return operations;
        }

        private string? ValidateTargetsWritable(List<JournalOperation> operations, string installRoot)
        {
            foreach (var operation in operations)
            {
                if (!SafeRelativePath.TryResolve(installRoot, operation.Path, out var target))
                    return $"Unsafe install path '{operation.Path}'.";

                if (!operation.ExistedBefore)
                    continue;

                try
                {
                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);
                    using var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return $"Cannot update '{operation.Path}' because the file is in use. " +
                           "Close the game and try again. (" + ex.Message + ")";
                }
            }
            return null;
        }

        private static async Task ApplyOperationAsync(JournalOperation operation, string installRoot,
            string payloadRoot, string backupRoot, CancellationToken ct)
        {
            if (!SafeRelativePath.TryResolve(installRoot, operation.Path, out var target))
                throw new InvalidDataException($"Unsafe install path '{operation.Path}'.");

            if (operation.ExistedBefore)
            {
                if (!SafeRelativePath.TryResolve(backupRoot, operation.Path, out var backup))
                    throw new InvalidDataException($"Unsafe backup path '{operation.Path}'.");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: true);
            }

            if (operation.Action == JournalAction.Delete)
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                return;
            }

            if (!SafeRelativePath.TryResolve(payloadRoot, operation.Path, out var staged))
                throw new InvalidDataException($"Unsafe staged path '{operation.Path}'.");
            if (!File.Exists(staged))
                throw new FileNotFoundException($"The staged file is missing: {operation.Path}", staged);

            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);

            // Copy next to the target and then move into place. The rename is the only
            // moment the live file changes, so a crash never leaves a truncated file.
            var temp = target + ".vellum-new-" + Guid.NewGuid().ToString("N");
            await using (var source = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, 81920, ct).ConfigureAwait(false);
            }

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

        private void PrepareBackupDirectory(string gameId, string backupRoot)
        {
            if (Directory.Exists(backupRoot))
                TryDeleteDirectory(backupRoot);
            Directory.CreateDirectory(backupRoot);
        }

        private async Task SaveJournalAsync(InstallJournal journal, CancellationToken ct)
        {
            PathHelper.EnsureDirectory(PathHelper.UpdateJournalsDir);
            var path = GetJournalPath(journal.GameId);
            var json = JsonSerializer.Serialize(journal, _json);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }

        private async Task SaveRollbackRecordAsync(InstallJournal journal, CancellationToken ct)
        {
            PathHelper.EnsureDirectory(PathHelper.UpdateRollbackDir);
            var path = GetRollbackRecordPath(journal.GameId);
            var json = JsonSerializer.Serialize(journal, _json);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }

        private static GameUpdateRecord GetOrCreateRecord(UpdatePersistedState state, string gameId)
        {
            if (!state.Games.TryGetValue(gameId, out var record))
            {
                record = new GameUpdateRecord { GameId = gameId };
                state.Games[gameId] = record;
            }
            return record;
        }

        private static string GetJournalPath(string gameId)
            => Path.Combine(PathHelper.UpdateJournalsDir, PathHelper.SanitizeFileName(gameId) + ".json");

        private static string GetRollbackRecordPath(string gameId)
            => Path.Combine(PathHelper.UpdateRollbackDir, PathHelper.SanitizeFileName(gameId) + ".json");

        private static void TryDeleteJournal(string gameId) => TryDelete(GetJournalPath(gameId));

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private const int BufferSize = 81920;

        private enum JournalState { Prepared, Applying, Committed }

        private enum JournalAction { Write, Delete }

        private sealed class JournalOperation
        {
            public string Path { get; set; } = string.Empty;
            public JournalAction Action { get; set; }
            public bool ExistedBefore { get; set; }
            public long OriginalSize { get; set; }
            public string BackupPath { get; set; } = string.Empty;
        }

        private sealed class InstallJournal
        {
            public int SchemaVersion { get; set; } = 1;
            public string GameId { get; set; } = string.Empty;
            public string InstallRoot { get; set; } = string.Empty;
            public string StagingRoot { get; set; } = string.Empty;
            public string BackupRoot { get; set; } = string.Empty;
            public string PreviousVersion { get; set; } = string.Empty;
            public string PreviousBuildId { get; set; } = string.Empty;
            public string NewVersion { get; set; } = string.Empty;
            public string NewBuildId { get; set; } = string.Empty;
            public JournalState State { get; set; }
            public int StartedOperations { get; set; }
            public DateTimeOffset StartedUtc { get; set; }
            public List<JournalOperation> Operations { get; set; } = new();
            public List<InstalledFileRecord> PreviousFiles { get; set; } = new();
            public List<InstalledFileRecord> NewFiles { get; set; } = new();
        }
    }
}
