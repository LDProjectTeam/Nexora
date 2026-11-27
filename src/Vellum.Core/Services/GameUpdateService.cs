using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Coordinates manifest checks, the persistent update queue and per-game update
    /// jobs. A game is never updated by two jobs or two launcher processes at once.
    /// </summary>
    public class GameUpdateService : IGameUpdateService, IDisposable
    {
        private readonly IUpdateManifestProvider _provider;
        private readonly IFileDiffService _diffService;
        private readonly IUpdateDownloadManager _downloader;
        private readonly IUpdateInstaller _installer;
        private readonly IUpdateStateStore _store;
        private readonly ISettingsService _settings;
        private readonly IGameLauncher _launcher;
        private readonly IDiskSpaceService? _diskSpace;
        private readonly ILogger<GameUpdateService> _logger;

        private readonly ConcurrentDictionary<string, GameUpdateStatus> _statuses =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, GameDefinition> _definitions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, RunningJob> _jobs =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, RunningJob> _waiters =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _queueLock = new();
        private readonly List<string> _queue = new();
        private readonly SemaphoreSlim _pumpGate = new(1, 1);
        private bool _initialized;
        private bool _queuePaused;
        private long _lastSaveTicks;
        private long _lastEmitTicks;

        public event EventHandler<GameUpdateStatus>? StatusChanged;

        public GameUpdateService(
            IUpdateManifestProvider provider,
            IFileDiffService diffService,
            IUpdateDownloadManager downloader,
            IUpdateInstaller installer,
            IUpdateStateStore store,
            ISettingsService settings,
            IGameLauncher launcher,
            ILogger<GameUpdateService> logger,
            IDiskSpaceService? diskSpace = null)
        {
            _provider = provider;
            _diffService = diffService;
            _downloader = downloader;
            _installer = installer;
            _store = store;
            _settings = settings;
            _launcher = launcher;
            _logger = logger;
            _diskSpace = diskSpace;
        }

        public IReadOnlyCollection<GameUpdateStatus> Statuses =>
            _statuses.Values.Select(s => s.Clone()).ToList();

        public GameUpdateStatus GetStatus(string gameId) =>
            _statuses.TryGetValue(gameId, out var status)
                ? status.Clone()
                : new GameUpdateStatus { GameId = gameId };

        public bool HasPendingUpdate(string gameId)
            => _statuses.TryGetValue(gameId, out var status) && status.HasUpdate;

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            if (_initialized)
                return;

            await _store.LoadAsync(ct).ConfigureAwait(false);
            var recovered = await _installer.RecoverAsync(ct).ConfigureAwait(false);

            foreach (var record in _store.State.Games.Values)
            {
                if (record.Phase is GameUpdatePhase.Downloading or GameUpdatePhase.Verifying
                    or GameUpdatePhase.Installing or GameUpdatePhase.WaitingForDownload)
                {
                    record.Phase = record.DownloadCompleted
                        ? GameUpdatePhase.UpdateAvailable
                        : GameUpdatePhase.Paused;
                    record.IsQueued = false;
                }

                if (recovered.Contains(record.GameId, StringComparer.OrdinalIgnoreCase)
                    && record.Phase == GameUpdatePhase.UpToDate)
                {
                    record.Phase = GameUpdatePhase.UpdateAvailable;
                }

                _statuses[record.GameId] = StatusFromRecord(record);
            }

            lock (_queueLock)
            {
                _queue.Clear();
                foreach (var entry in _store.State.Queue.OrderBy(e => e.Order))
                    _queue.Add(entry.GameId);
            }

            _initialized = true;

            foreach (var record in _store.State.Games.Values)
            {
                if (record.Phase is GameUpdatePhase.UpToDate or GameUpdatePhase.UpdateAvailable
                    or GameUpdatePhase.Paused or GameUpdatePhase.Error)
                {
                    Emit(StatusFromRecord(record), force: true);
                }
            }

            if (_store.State.QueueWasRunning && _queue.Count > 0)
            {
                _queuePaused = false;
                _ = PumpQueueAsync(CancellationToken.None);
            }

            await _store.SaveAsync(ct).ConfigureAwait(false);
        }

        public async Task<GameUpdateCheckResult> CheckForUpdateAsync(
            GameDefinition definition, GameInstallation installation, bool force = false,
            CancellationToken ct = default)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            _definitions[definition.Id] = definition;

            var result = new GameUpdateCheckResult { GameId = definition.Id };
            var url = ResolveManifestUrl(definition, out var channel, out var sourceError);
            var record = GetOrCreateRecord(definition.Id);
            record.InstallPath = installation.InstallPath;
            record.Channel = channel;

            if (string.IsNullOrWhiteSpace(url))
            {
                result.SourceConfigured = false;
                result.Status = new GameUpdateStatus
                {
                    GameId = definition.Id,
                    CurrentVersion = installation.InstalledVersion,
                    CurrentBuildId = installation.InstalledBuildId,
                    Phase = GameUpdatePhase.Unknown,
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = sourceError
                };
                _statuses[definition.Id] = result.Status;
                return result;
            }

            if (_jobs.ContainsKey(definition.Id))
            {
                result.Status = GetStatus(definition.Id);
                return result;
            }

            var priorStatus = GetStatus(definition.Id);
            var checking = priorStatus.Clone();
            checking.Phase = GameUpdatePhase.Checking;
            checking.CurrentVersion = installation.InstalledVersion;
            checking.CurrentBuildId = installation.InstalledBuildId;
            _statuses[definition.Id] = checking;
            Emit(checking, force: true);

            try
            {
                var manifest = await _provider.GetGameManifestAsync(definition.Id, url, ct)
                    .ConfigureAwait(false);

                var isUpdate = UpdateVersion.IsBuildUpdate(
                    manifest.Version, manifest.BuildId,
                    installation.InstalledVersion, installation.InstalledBuildId);

                FileDiffResult? diff = null;
                if (!isUpdate)
                {
                    diff = await _diffService.ComputeAsync(
                        manifest, installation.InstallPath, record.InstalledFiles,
                        verifyUnchangedFiles: force, ct: ct).ConfigureAwait(false);

                    if (diff.IsUpToDate)
                    {
                        var upToDate = new GameUpdateStatus
                        {
                            GameId = definition.Id,
                            CurrentVersion = manifest.Version,
                            CurrentBuildId = manifest.BuildId,
                            Phase = GameUpdatePhase.UpToDate,
                            LastCheckedUtc = DateTimeOffset.UtcNow
                        };
                        _statuses[definition.Id] = upToDate;
                        MergeRecord(record, upToDate);
                        record.PendingManifestUrl = null;
                        record.PendingVersion = null;
                        record.PendingBuildId = null;
                        record.CurrentVersion = manifest.Version;
                        record.BuildId = manifest.BuildId;
                        record.InstalledFiles = manifest.Files.Select(f => new InstalledFileRecord
                        {
                            Path = f.Path,
                            Size = f.Size,
                            Sha256 = f.Sha256
                        }).ToList();
                        await _store.DeleteManifestAsync(definition.Id).ConfigureAwait(false);
                        RemoveFromQueue(definition.Id);
                        await _store.SaveAsync(ct).ConfigureAwait(false);
                        Emit(upToDate, force: true);
                        result.Status = upToDate;
                        return result;
                    }

                    // Same build but local files are missing/corrupted/changed:
                    // offer a repair using the same manifest.
                    isUpdate = true;
                }

                diff ??= await _diffService.ComputeAsync(
                    manifest, installation.InstallPath, record.InstalledFiles,
                    verifyUnchangedFiles: force, ct: ct).ConfigureAwait(false);

                var status = new GameUpdateStatus
                {
                    GameId = definition.Id,
                    CurrentVersion = installation.InstalledVersion,
                    CurrentBuildId = installation.InstalledBuildId,
                    AvailableVersion = manifest.Version,
                    AvailableBuildId = manifest.BuildId,
                    Phase = GameUpdatePhase.UpdateAvailable,
                    UpdateSizeBytes = diff.BytesToDownload,
                    ChangedFileCount = diff.ModifiedFileCount + diff.CorruptedFileCount,
                    AddedFileCount = diff.AddedFileCount,
                    DeletedFileCount = diff.DeletedFileCount,
                    TotalBytes = diff.BytesToDownload,
                    LastCheckedUtc = DateTimeOffset.UtcNow
                };

                _statuses[definition.Id] = status;
                record.PendingManifestUrl = url;
                record.PendingVersion = manifest.Version;
                record.PendingBuildId = manifest.BuildId;
                record.Phase = GameUpdatePhase.UpdateAvailable;
                record.UpdateSizeBytes = diff.BytesToDownload;
                record.ChangedFileCount = status.ChangedFileCount;
                record.AddedFileCount = status.AddedFileCount;
                record.DeletedFileCount = status.DeletedFileCount;
                record.TotalBytes = diff.BytesToDownload;
                record.DownloadedBytes = 0;
                record.DownloadCompleted = false;
                record.LastCheckedUtc = DateTimeOffset.UtcNow;
                record.ErrorMessage = null;
                await _store.SaveManifestAsync(definition.Id, manifest, ct).ConfigureAwait(false);
                await _store.SaveAsync(ct).ConfigureAwait(false);
                Emit(status, force: true);

                result.SourceConfigured = true;
                result.UpdateAvailable = true;
                result.Manifest = manifest;
                result.Diff = diff;
                result.Status = status;

                if (_settings.Settings.AutoUpdateGames && !_queuePaused)
                    Enqueue(definition.Id, front: false);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update check failed for {GameId}.", definition.Id);
                var previous = priorStatus.Clone();
                previous.Phase = GameUpdatePhase.Error;
                previous.ErrorKind = GameUpdateErrorKind.Check;
                previous.ErrorMessage = ex.Message;
                previous.LastCheckedUtc = DateTimeOffset.UtcNow;
                _statuses[definition.Id] = previous;
                record.Phase = previous.HasUpdate ? GameUpdatePhase.UpdateAvailable : GameUpdatePhase.Error;
                record.LastCheckedUtc = DateTimeOffset.UtcNow;
                record.ErrorKind = GameUpdateErrorKind.Check;
                _store.RecordError(definition.Id, ex.Message);
                await _store.SaveAsync(ct).ConfigureAwait(false);
                Emit(previous, force: true);

                result.SourceConfigured = true;
                result.ErrorMessage = ex.Message;
                result.Status = previous;
                return result;
            }
        }

        public async Task CheckAllAsync(IEnumerable<GameDefinition> definitions, CancellationToken ct = default)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var list = definitions.ToList();
            foreach (var definition in list)
                _definitions[definition.Id] = definition;

            var gate = new SemaphoreSlim(2, 2);
            var tasks = list.Select(async definition =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var record = GetOrCreateRecord(definition.Id);
                    if (string.IsNullOrWhiteSpace(record.InstallPath) || _jobs.ContainsKey(definition.Id))
                        return;

                    var installation = new GameInstallation
                    {
                        GameId = definition.Id,
                        InstallPath = record.InstallPath,
                        InstalledVersion = record.CurrentVersion,
                        InstalledBuildId = record.BuildId
                    };
                    await CheckForUpdateAsync(definition, installation, force: false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background update check failed for {GameId}.", definition.Id);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public Task<OperationResult> StartUpdateAsync(string gameId,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
            => StartOrResumeAsync(gameId, progress, ct, front: true);

        public Task<OperationResult> ResumeUpdateAsync(string gameId,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
            => StartOrResumeAsync(gameId, progress, ct, front: false);

        private async Task<OperationResult> StartOrResumeAsync(string gameId,
            IProgress<DownloadProgress>? progress, CancellationToken ct, bool front)
        {
            await InitializeAsync(ct).ConfigureAwait(false);

            if (_jobs.TryGetValue(gameId, out var existing))
                return await existing.Completion.Task.ConfigureAwait(false);

            var status = GetStatus(gameId);
            if (!status.HasUpdate && status.Phase != GameUpdatePhase.Error)
                return OperationResult.Fail("No update is available for this game.");

            GetOrCreateRecord(gameId);
            _queuePaused = false;
            var waiter = new RunningJob(gameId, progress);
            if (!_waiters.TryAdd(gameId, waiter))
            {
                return await _waiters[gameId].Completion.Task.ConfigureAwait(false);
            }
            Enqueue(gameId, front: front);

            try
            {
                await waiter.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
                return waiter.Result ?? OperationResult.Fail("Update did not complete.");
            }
            catch (OperationCanceledException)
            {
                await PauseAsync(gameId).ConfigureAwait(false);
                return OperationResult.Cancelled("Update paused.");
            }
        }

        public async Task PauseAsync(string gameId)
        {
            await InitializeAsync().ConfigureAwait(false);
            if (_jobs.TryGetValue(gameId, out var job))
                job.Cts.Cancel();

            RemoveFromQueue(gameId);
            if (_statuses.TryGetValue(gameId, out var status) && status.HasUpdate)
            {
                status.Phase = GameUpdatePhase.Paused;
                status.SpeedBytesPerSec = 0;
                status.SecondsRemaining = -1;
                Emit(status, force: true);
            }

            var record = GetOrCreateRecord(gameId);
            record.Phase = GameUpdatePhase.Paused;
            record.IsQueued = false;
            await _store.SaveAsync().ConfigureAwait(false);
        }

        public async Task RemoveAsync(string gameId)
        {
            await InitializeAsync().ConfigureAwait(false);
            if (_jobs.TryGetValue(gameId, out var job))
                job.Cts.Cancel();

            RemoveFromQueue(gameId);
            var record = GetOrCreateRecord(gameId);
            record.PendingManifestUrl = null;
            record.PendingVersion = null;
            record.PendingBuildId = null;
            record.Phase = GameUpdatePhase.Unknown;
            record.IsQueued = false;
            record.DownloadCompleted = false;
            record.DownloadedBytes = 0;
            record.TotalBytes = 0;
            await _store.DeleteManifestAsync(gameId).ConfigureAwait(false);

            var status = GetStatus(gameId);
            status.Phase = GameUpdatePhase.Unknown;
            status.AvailableVersion = null;
            status.AvailableBuildId = null;
            status.UpdateSizeBytes = 0;
            status.DownloadedBytes = 0;
            status.TotalBytes = 0;
            status.SpeedBytesPerSec = 0;
            status.SecondsRemaining = -1;
            _statuses[gameId] = status;
            Emit(status, force: true);

            if (!string.IsNullOrWhiteSpace(record.InstallPath))
            {
                var staging = PathHelper.GetUpdateStagingPath(record.InstallPath, gameId);
                TryDeleteDirectory(staging);
            }
            await _store.SaveAsync().ConfigureAwait(false);
        }

        public async Task UpdateAllAsync(CancellationToken ct = default)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var candidates = _statuses.Values
                .Where(s => s.Phase is GameUpdatePhase.UpdateAvailable or GameUpdatePhase.Paused
                    || (s.Phase == GameUpdatePhase.Error && s.AvailableVersion != null))
                .OrderBy(s => GetQueueOrder(s.GameId))
                .ToList();
            foreach (var status in candidates)
                Enqueue(status.GameId, front: false);
            await PumpQueueAsync(ct).ConfigureAwait(false);
        }

        public async Task PauseAllAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            _queuePaused = true;
            foreach (var job in _jobs.Values)
                job.Cts.Cancel();
            foreach (var record in _store.State.Games.Values)
            {
                if (record.Phase is GameUpdatePhase.WaitingForDownload or GameUpdatePhase.Downloading)
                {
                    record.Phase = GameUpdatePhase.Paused;
                    record.IsQueued = false;
                    var status = GetStatus(record.GameId);
                    status.Phase = GameUpdatePhase.Paused;
                    _statuses[record.GameId] = status;
                    Emit(status, force: true);
                }
            }
            _store.State.QueueWasRunning = false;
            await _store.SaveAsync().ConfigureAwait(false);
        }

        public async Task ResumeAllAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            _queuePaused = false;
            foreach (var record in _store.State.Games.Values)
            {
                if (record.Phase is GameUpdatePhase.Paused or GameUpdatePhase.Error
                    && !string.IsNullOrWhiteSpace(record.PendingVersion))
                {
                    Enqueue(record.GameId, front: false);
                }
            }
            await PumpQueueAsync().ConfigureAwait(false);
        }

        public void MoveInQueue(string gameId, int delta)
        {
            lock (_queueLock)
            {
                var index = _queue.FindIndex(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return;
                var target = Math.Clamp(index + delta, 0, _queue.Count - 1);
                if (target == index)
                    return;
                _queue.RemoveAt(index);
                _queue.Insert(target, gameId);
                ReindexQueue();
            }
            _ = PersistQueueAsync();
        }

        public void Promote(string gameId)
        {
            lock (_queueLock)
            {
                var index = _queue.FindIndex(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
                if (index <= 0)
                    return;
                _queue.RemoveAt(index);
                _queue.Insert(0, gameId);
                ReindexQueue();
            }
            _ = PersistQueueAsync();
        }

        public async void NotifyConcurrencyChanged()
        {
            try
            {
                await PumpQueueAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not re-evaluate the update queue.");
            }
        }

        public async Task ShutdownAsync()
        {
            var wasRunning = !_queuePaused && (_jobs.Count > 0 || QueueCount > 0);
            _queuePaused = true;
            foreach (var job in _jobs.Values)
                job.Cts.Cancel();

            try
            {
                await Task.WhenAll(_jobs.Values.Select(j => j.Completion.Task))
                    .WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
            catch
            {
                // Shutdown must not block forever on a stalled network operation.
            }

            _store.State.QueueWasRunning = wasRunning;
            await _store.SaveAsync().ConfigureAwait(false);
        }

        public async Task<RollbackOutcome> RollbackAsync(string gameId, CancellationToken ct = default)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var record = GetOrCreateRecord(gameId);
            if (string.IsNullOrWhiteSpace(record.InstallPath))
                return new RollbackOutcome { Success = false, ErrorMessage = "The game install path is unknown." };

            var outcome = await _installer.RollbackAsync(gameId, record.InstallPath, ct).ConfigureAwait(false);
            if (outcome.Success)
            {
                var status = GetStatus(gameId);
                status.CurrentVersion = outcome.RestoredVersion ?? record.CurrentVersion;
                status.CurrentBuildId = record.BuildId;
                status.AvailableVersion = null;
                status.Phase = GameUpdatePhase.UpdateAvailable;
                status.ErrorMessage = "Rolled back to the previous build.";
                _statuses[gameId] = status;
                Emit(status, force: true);
            }
            return outcome;
        }

        private async Task PumpQueueAsync(CancellationToken ct = default)
        {
            if (!_initialized)
                return;

            await _pumpGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_queuePaused)
                    return;

                var maxConcurrent = Math.Clamp(_settings.Settings.MaxConcurrentDownloads, 1, 5);
                while (_jobs.Count < maxConcurrent)
                {
                    string? next;
                    lock (_queueLock)
                    {
                        next = _queue.FirstOrDefault(id =>
                            !_jobs.ContainsKey(id) && _store.State.Games.TryGetValue(id, out var r)
                            && r.PendingVersion != null);
                        if (next != null)
                            _queue.Remove(next);
                    }

                    if (next == null)
                        break;

                    StartJob(next);
                }
            }
            finally
            {
                _pumpGate.Release();
            }
        }

        private void StartJob(string gameId)
        {
            var status = GetStatus(gameId);
            status.Phase = GameUpdatePhase.Downloading;
            status.LastCheckedUtc ??= DateTimeOffset.UtcNow;
            _statuses[gameId] = status;
            Emit(status, force: true);

            var record = GetOrCreateRecord(gameId);
            record.IsQueued = false;
            var job = _waiters.TryRemove(gameId, out var waiter)
                ? waiter
                : new RunningJob(gameId, null);
            if (!_jobs.TryAdd(gameId, job))
                return;

            job.Execution = Task.Run(() => RunJobAsync(gameId, job));
        }

        private async Task RunJobAsync(string gameId, RunningJob job)
        {
            IDisposable? processLock = null;
            try
            {
                processLock = await _store.TryAcquireGameLockAsync(gameId, job.Cts.Token).ConfigureAwait(false);
                if (processLock == null)
                    throw new InvalidOperationException(
                        "This game is already being updated by another process.");

                if (_launcher.IsRunning(gameId))
                    throw new InvalidOperationException(
                        "Close the game before installing an update.");

                var record = GetOrCreateRecord(gameId);
                if (string.IsNullOrWhiteSpace(record.InstallPath) || !Directory.Exists(record.InstallPath))
                    throw new DirectoryNotFoundException("The game install folder no longer exists.");

                var manifest = await _store.LoadManifestAsync(gameId, job.Cts.Token).ConfigureAwait(false);
                if (manifest == null && !string.IsNullOrWhiteSpace(record.PendingManifestUrl))
                {
                    manifest = await _provider.GetGameManifestAsync(gameId, record.PendingManifestUrl, job.Cts.Token)
                        .ConfigureAwait(false);
                    await _store.SaveManifestAsync(gameId, manifest, job.Cts.Token).ConfigureAwait(false);
                }
                if (manifest == null)
                    throw new InvalidOperationException("The pending update manifest is not available.");

                // Recompute against the live tree right before downloading. Files that
                // changed since the check are added to the download set.
                var diff = await _diffService.ComputeAsync(
                    manifest, record.InstallPath, record.InstalledFiles,
                    verifyUnchangedFiles: true, ct: job.Cts.Token).ConfigureAwait(false);

                var plan = new GameUpdatePlan
                {
                    Manifest = manifest,
                    Diff = diff,
                    InstallRoot = record.InstallPath,
                    DisplayName = record.GameId,
                    ManifestUrl = record.PendingManifestUrl ?? string.Empty,
                    PreviousManifest = record.InstalledFiles.ToList()
                };

                record.Phase = GameUpdatePhase.Downloading;
                record.UpdateSizeBytes = diff.BytesToDownload;
                record.TotalBytes = diff.BytesToDownload;
                record.ChangedFileCount = diff.ModifiedFileCount + diff.CorruptedFileCount;
                record.AddedFileCount = diff.AddedFileCount;
                record.DeletedFileCount = diff.DeletedFileCount;
                record.DownloadedBytes = 0;
                await _store.SaveAsync(job.Cts.Token).ConfigureAwait(false);

                var staging = PathHelper.GetUpdateStagingPath(record.InstallPath, gameId);
                if (_diskSpace != null)
                {
                    var free = _diskSpace.GetAvailableFreeSpace(staging, out var drive);
                    var staged = Directory.Exists(staging)
                        ? Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                        : 0L;
                    var required = diff.BytesToDownload + 64L * 1024 * 1024;
                    if (free + staged < required)
                    {
                        throw new IOException(
                            $"Not enough free space on {drive} for the update. " +
                            $"Required: {PathHelper.FormatBytes(required)}, available: {PathHelper.FormatBytes(free + staged)}.");
                    }
                }

                var downloadProgress = CreateProgressBridge(gameId, job.Progress, GameUpdatePhase.Downloading);
                var download = await _downloader.DownloadAsync(
                    plan, staging,
                    _settings.Settings.MaxDownloadSpeedBps,
                    _settings.Settings.MaxConcurrentUpdateFiles,
                    downloadProgress, job.Cts.Token).ConfigureAwait(false);

                record.DownloadedBytes = download.DownloadedBytes;
                record.TotalBytes = download.TotalBytes > 0 ? download.TotalBytes : record.TotalBytes;

                if (download.Paused || job.Cts.IsCancellationRequested)
                {
                    record.Phase = GameUpdatePhase.Paused;
                    record.DownloadCompleted = false;
                    job.Result = OperationResult.Cancelled("Update paused.");
                    EmitPhase(gameId, GameUpdatePhase.Paused, record.DownloadedBytes, record.TotalBytes);
                    await _store.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (!download.Success)
                    throw new IOException(download.ErrorMessage ?? "The update download failed.");

                record.DownloadCompleted = true;
                record.Phase = GameUpdatePhase.Verifying;
                EmitPhase(gameId, GameUpdatePhase.Verifying, record.DownloadedBytes, record.TotalBytes);
                await _store.SaveAsync(job.Cts.Token).ConfigureAwait(false);

                record.Phase = GameUpdatePhase.Installing;
                EmitPhase(gameId, GameUpdatePhase.Installing, record.DownloadedBytes, record.TotalBytes);
                await _store.SaveAsync(job.Cts.Token).ConfigureAwait(false);

                var installProgress = CreateProgressBridge(gameId, job.Progress, GameUpdatePhase.Installing);
                var apply = await _installer.ApplyAsync(
                    plan, staging, _store.State,
                    beforeApply: () =>
                    {
                        if (_launcher.IsRunning(gameId))
                            throw new InvalidOperationException("The game was started during the update.");
                        return Task.CompletedTask;
                    },
                    installProgress, job.Cts.Token).ConfigureAwait(false);

                if (!apply.Success)
                    throw new InvalidOperationException(apply.ErrorMessage ?? "The update could not be installed.");

                TryDeleteDirectory(staging);
                record.Phase = GameUpdatePhase.UpToDate;
                record.DownloadedBytes = 0;
                record.TotalBytes = 0;
                record.DownloadCompleted = false;
                record.RollbackAvailable = true;
                record.LastUpdatedUtc = DateTimeOffset.UtcNow;
                job.Result = OperationResult.Ok(record.InstallPath);

                var done = new GameUpdateStatus
                {
                    GameId = gameId,
                    CurrentVersion = manifest.Version,
                    CurrentBuildId = manifest.BuildId,
                    Phase = GameUpdatePhase.UpToDate,
                    LastCheckedUtc = DateTimeOffset.UtcNow
                };
                _statuses[gameId] = done;
                Emit(done, force: true);
            }
            catch (OperationCanceledException)
            {
                var record = GetOrCreateRecord(gameId);
                record.Phase = GameUpdatePhase.Paused;
                job.Result = OperationResult.Cancelled("Update paused.");
                EmitPhase(gameId, GameUpdatePhase.Paused, record.DownloadedBytes, record.TotalBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update job failed for {GameId}.", gameId);
                var record = GetOrCreateRecord(gameId);
                var failedDuringInstall = record.Phase is GameUpdatePhase.Installing or GameUpdatePhase.Verifying;
                record.Phase = GameUpdatePhase.Error;
                record.ErrorMessage = ex.Message;
                record.ErrorKind = failedDuringInstall ? GameUpdateErrorKind.Install : GameUpdateErrorKind.Download;
                _store.RecordError(gameId, ex.Message);
                job.Result = OperationResult.Fail(ex.Message, ex);
                var status = GetStatus(gameId);
                status.Phase = GameUpdatePhase.Error;
                status.ErrorKind = failedDuringInstall
                    ? GameUpdateErrorKind.Install
                    : GameUpdateErrorKind.Download;
                status.ErrorMessage = ex.Message;
                _statuses[gameId] = status;
                Emit(status, force: true);
            }
            finally
            {
                processLock?.Dispose();
                _jobs.TryRemove(gameId, out _);
                RemoveFromQueue(gameId);
                if (!_queuePaused && QueueCount == 0 && _jobs.IsEmpty)
                    _store.State.QueueWasRunning = false;
                try { await _store.SaveAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                job.Completion.TrySetResult(job.Result ?? OperationResult.Fail("Update did not complete."));
                _ = PumpQueueAsync();
            }
        }

        private IProgress<DownloadProgress> CreateProgressBridge(string gameId,
            IProgress<DownloadProgress>? target, GameUpdatePhase phase)
        {
            return new Progress<DownloadProgress>(p =>
            {
                target?.Report(p);
                var record = GetOrCreateRecord(gameId);
                record.DownloadedBytes = p.BytesDownloaded;
                if (p.TotalBytes > 0)
                    record.TotalBytes = p.TotalBytes;

                var status = GetStatus(gameId);
                status.Phase = phase;
                status.DownloadedBytes = p.BytesDownloaded;
                status.TotalBytes = p.TotalBytes;
                status.SpeedBytesPerSec = p.SpeedBytesPerSec;
                status.SecondsRemaining = p.SecondsRemaining;
                status.UpdateSizeBytes = p.TotalBytes;
                _statuses[gameId] = status;
                Emit(status, force: false);
                MaybePersistProgress();
            });
        }

        private void EmitPhase(string gameId, GameUpdatePhase phase, long downloaded, long total)
        {
            var status = GetStatus(gameId);
            status.Phase = phase;
            status.DownloadedBytes = downloaded;
            status.TotalBytes = total;
            status.SpeedBytesPerSec = 0;
            status.SecondsRemaining = -1;
            _statuses[gameId] = status;
            Emit(status, force: true);
        }

        private void Emit(GameUpdateStatus status, bool force)
        {
            if (!force)
            {
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastEmitTicks);
                if (now - last < 250)
                    return;
                Interlocked.Exchange(ref _lastEmitTicks, now);
            }
            StatusChanged?.Invoke(this, status.Clone());
        }

        private void MaybePersistProgress()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastSaveTicks);
            if (now - last < 2000)
                return;
            Interlocked.Exchange(ref _lastSaveTicks, now);
            _ = PersistProgressAsync();
        }

        private async Task PersistProgressAsync()
        {
            try { await _store.SaveAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not persist update progress."); }
        }

        private void Enqueue(string gameId, bool front)
        {
            if (_jobs.ContainsKey(gameId))
                return;

            lock (_queueLock)
            {
                var index = _queue.FindIndex(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    if (front)
                        _queue.Insert(0, gameId);
                    else
                        _queue.Add(gameId);
                }
                else if (front && index > 0)
                {
                    _queue.RemoveAt(index);
                    _queue.Insert(0, gameId);
                }
            }

            var record = GetOrCreateRecord(gameId);
            record.IsQueued = true;
            record.Phase = GameUpdatePhase.WaitingForDownload;
            _store.State.QueueWasRunning = true;
            var status = GetStatus(gameId);
            status.Phase = GameUpdatePhase.WaitingForDownload;
            _statuses[gameId] = status;
            Emit(status, force: true);

            _ = PersistQueueAsync();
            _ = PumpQueueAsync();
        }

        private void RemoveFromQueue(string gameId)
        {
            lock (_queueLock)
            {
                _queue.RemoveAll(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
                ReindexQueue();
            }
        }

        private void ReindexQueue()
        {
            _store.State.Queue = _queue.Select((id, index) => new GameUpdateQueueEntry
            {
                GameId = id,
                Order = index
            }).ToList();
        }

        private async Task PersistQueueAsync()
        {
            try
            {
                lock (_queueLock)
                    ReindexQueue();
                await _store.SaveAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist the update queue.");
            }
        }

        private int QueueCount { get { lock (_queueLock) return _queue.Count; } }

        private int GetQueueOrder(string gameId)
        {
            lock (_queueLock)
            {
                var index = _queue.FindIndex(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            }
        }

        private GameUpdateRecord GetOrCreateRecord(string gameId)
        {
            if (!_store.State.Games.TryGetValue(gameId, out var record))
            {
                record = new GameUpdateRecord { GameId = gameId };
                _store.State.Games[gameId] = record;
            }
            return record;
        }

        private static GameUpdateStatus StatusFromRecord(GameUpdateRecord record) => new()
        {
            GameId = record.GameId,
            CurrentVersion = record.CurrentVersion,
            CurrentBuildId = record.BuildId,
            AvailableVersion = record.PendingVersion,
            AvailableBuildId = record.PendingBuildId,
            Phase = record.Phase,
            UpdateSizeBytes = record.UpdateSizeBytes,
            ChangedFileCount = record.ChangedFileCount,
            AddedFileCount = record.AddedFileCount,
            DeletedFileCount = record.DeletedFileCount,
            DownloadedBytes = record.DownloadedBytes,
            TotalBytes = record.TotalBytes,
            LastCheckedUtc = record.LastCheckedUtc,
            ErrorMessage = record.ErrorMessage,
            ErrorKind = record.ErrorKind
        };

        private static void MergeRecord(GameUpdateRecord record, GameUpdateStatus status)
        {
            record.Phase = status.Phase;
            record.CurrentVersion = status.CurrentVersion;
            record.BuildId = status.CurrentBuildId;
            record.PendingVersion = status.AvailableVersion;
            record.PendingBuildId = status.AvailableBuildId;
            record.UpdateSizeBytes = status.UpdateSizeBytes;
            record.ChangedFileCount = status.ChangedFileCount;
            record.AddedFileCount = status.AddedFileCount;
            record.DeletedFileCount = status.DeletedFileCount;
            record.DownloadedBytes = status.DownloadedBytes;
            record.TotalBytes = status.TotalBytes;
            record.LastCheckedUtc = status.LastCheckedUtc;
            record.ErrorMessage = status.ErrorMessage;
            record.ErrorKind = status.ErrorKind;
        }

        private string? ResolveManifestUrl(GameDefinition definition, out string channel, out string? error)
        {
            error = null;
            channel = NormalizeChannel(_settings.Settings.UpdateChannel);

            var template = definition.ManifestUrl;
            if (string.IsNullOrWhiteSpace(template))
            {
                template = definition.Updates
                    .Where(u => !string.IsNullOrWhiteSpace(u.ManifestUrl))
                    .OrderByDescending(u => u.Version, StringComparer.OrdinalIgnoreCase)
                    .Select(u => u.ManifestUrl)
                    .FirstOrDefault();
            }

            if (!string.IsNullOrWhiteSpace(template))
            {
                var expanded = template
                    .Replace("{channel}", channel, StringComparison.OrdinalIgnoreCase)
                    .Replace("{gameId}", definition.Id, StringComparison.OrdinalIgnoreCase);
                if (Uri.TryCreate(expanded, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    return uri.AbsoluteUri;
                }

                error = "The configured manifest URL is not a valid HTTP/HTTPS URL.";
                return null;
            }

            var baseUrl = _settings.Settings.UpdateManifestBaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var combined = $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(definition.Id)}/{channel}/manifest.json";
            if (!Uri.TryCreate(combined, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                error = "The update manifest base URL in settings is invalid.";
                return null;
            }
            return baseUri.AbsoluteUri;
        }

        private static string NormalizeChannel(string? channel)
            => string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        public void Dispose()
        {
            foreach (var job in _jobs.Values)
                job.Cts.Cancel();
            _pumpGate.Dispose();
        }

        private sealed class RunningJob
        {
            public RunningJob(string gameId, IProgress<DownloadProgress>? progress)
            {
                GameId = gameId;
                Progress = progress;
            }

            public string GameId { get; }
            public IProgress<DownloadProgress>? Progress { get; }
            public CancellationTokenSource Cts { get; } = new();
            public TaskCompletionSource<OperationResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task? Execution { get; set; }
            public OperationResult? Result { get; set; }
        }



    }
}
