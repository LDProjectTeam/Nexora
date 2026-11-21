using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Central game management service. Coordinates the full install/update/uninstall
    /// pipeline and maintains the library state (persisted to library.json).
    /// </summary>
    public class GameManager : IGameManager
    {
        private readonly ILogger<GameManager> _logger;
        private readonly IGameCatalog _catalog;
        private readonly IDownloadManager _downloadManager;
        private readonly IInstaller _installer;
        private readonly IUpdater _updater;
        private readonly IFileVerifier _verifier;
        private readonly ISettingsService _settings;
        private readonly IDiskSpaceService _diskSpaceService;
        private static readonly string[] RutorFallbackTrackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://exodus.desync.com:6969/announce",
            "http://tracker.opentrackr.org:1337/announce",
            "http://tracker.qu.ax:6969/announce",
            "http://tracker.dler.org:6969/announce",
            "http://tracker.renfei.net:8080/announce"
        };

        private readonly ConcurrentDictionary<string, GameInstallation> _installations = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _activeOperations = new();
        private readonly ConcurrentDictionary<string, (GameReleaseCandidate Release, string TargetPath)> _pausedReleases = new();
        /// <summary>
        /// IDs whose download was just removed. State/progress writes from an operation
        /// that is still unwinding are suppressed so a removed card cannot reappear.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte> _removedDownloads = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _librarySaveLock = new(1, 1);
        private long _lastProgressSaveTicks;
        private string? _lastSavedLibraryJson;

        private sealed class QueuedDownloadWaiter
        {
            public string GameId { get; init; } = string.Empty;
            public GameInstallation Installation { get; init; } = null!;
            public TaskCompletionSource<bool> SlotGranted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly object _queueLock = new();
        private readonly HashSet<string> _activeDownloadSlotIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<QueuedDownloadWaiter> _waitingQueue = new();
        private int _nextQueueOrder;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter() }
        };

        // Raised when an installation's state changes
        public event EventHandler<GameInstallation>? InstallationStateChanged;

        public IReadOnlyDictionary<string, GameInstallation> Installations => _installations;

        public GameManager(
            ILogger<GameManager> logger,
            IGameCatalog catalog,
            IDownloadManager downloadManager,
            IInstaller installer,
            IUpdater updater,
            IFileVerifier verifier,
            ISettingsService settings,
            IDiskSpaceService diskSpaceService)
        {
            _logger = logger;
            _catalog = catalog;
            _downloadManager = downloadManager;
            _installer = installer;
            _updater = updater;
            _verifier = verifier;
            _settings = settings;
            _diskSpaceService = diskSpaceService;
        }

        public GameInstallation GetInstallation(string gameId)
        {
            if (_installations.TryGetValue(gameId, out var inst))
                return inst;
            return new GameInstallation { GameId = gameId, State = InstallState.NotInstalled };
        }

        public void TrackInstallation(GameInstallation installation)
        {
            _removedDownloads.TryRemove(installation.GameId, out _);
            _installations[installation.GameId] = installation;
        }

        public async Task<OperationResult> InstallAsync(
            string gameId,
            string targetPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var definition = await _catalog.GetGameAsync(gameId, ct).ConfigureAwait(false);
            if (definition == null)
                return OperationResult.Fail($"Game '{gameId}' not found in catalog.");

            if (string.IsNullOrWhiteSpace(definition.InstallSubfolder))
                definition.InstallSubfolder = PathHelper.SanitizeFileName(definition.Name);
            if (string.IsNullOrWhiteSpace(definition.InstallSubfolder))
                definition.InstallSubfolder = PathHelper.SanitizeFileName(gameId);

            // A pause can still be unwinding when the user presses Continue. Wait for the
            // previous operation to release its slot, otherwise the resume is rejected
            // with "already being installed" and the Continue button looks dead.
            await WaitForPreviousOperationAsync(gameId, ct).ConfigureAwait(false);

            // A fresh user-initiated attempt supersedes an earlier removal tombstone.
            _removedDownloads.TryRemove(gameId, out _);

            // ── Pre-download disk space check ──
            var stagingPath = PathHelper.GetStagingPath(targetPath, gameId);
            var existingInst = GetInstallation(gameId);
            var isResume = existingInst?.State == InstallState.Paused;
            var spaceCheck = _diskSpaceService.CalculateRequirement(definition, targetPath);
            // Count the payload already sitting on the drive (torrent preallocation makes
            // a paused download occupy its final size), not just the verified-piece counter.
            var alreadyDownloadedBytes = PathHelper.GetPayloadBytesOnDisk(
                stagingPath, existingInst?.DownloadedBytes ?? 0);
            var canDownload = spaceCheck.HasEnoughSpaceForDownload(alreadyDownloadedBytes);

            if (!canDownload)
            {
                var summary = spaceCheck.GetDownloadSummaryText(alreadyDownloadedBytes);
                _logger.LogWarning("Insufficient disk space to download {GameId}: {Summary}", gameId, summary);
                return OperationResult.Fail(summary);
            }

            var operationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_activeOperations.TryAdd(gameId, operationFinished))
                return OperationResult.Fail("This game is already being installed.");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts[gameId] = cts;

            var installation = _installations.GetOrAdd(gameId, id => new GameInstallation
            {
                GameId = id,
                InstallPath = Path.Combine(targetPath, definition.InstallSubfolder),
            });

            installation.InstallPath = Path.Combine(targetPath, definition.InstallSubfolder);
            installation.DisplayName = definition.Name;
            installation.TargetLibraryPath = targetPath;
            installation.WasDownloadingWhenClosed = false;
            installation.LastErrorMessage = null;
            installation.CompletedInSession = false;
            EnsureQueueOrder(installation);

            // A fresh attempt downloads again; a resume keeps the completed-download marker.
            if (!isResume)
                installation.DownloadCompleted = false;

            var trackedProgress = new ProgressRelay(installation, progress, this);

            try
            {
                await AcquireDownloadSlotAsync(installation, trackedProgress, cts.Token).ConfigureAwait(false);

                // --- Download phase ---
                SetState(installation, InstallState.Downloading);
                await SaveLibraryAsync().ConfigureAwait(false);

                PathHelper.EnsureDirectory(stagingPath);

                // Skip the download when a previous attempt already fetched the payload
                // (e.g. the user paused while the game was being unpacked).
                var downloadResult = installation.DownloadCompleted
                    ? OperationResult.Ok()
                    : await _downloadManager.DownloadAsync(
                        definition, stagingPath, trackedProgress, cts.Token).ConfigureAwait(false);

                if (!downloadResult.Success)
                {
                    var isPaused = cts.IsCancellationRequested;
                    installation.IsQueued = false;
                    if (!isPaused)
                        installation.LastErrorMessage = downloadResult.ErrorMessage;
                    SetState(installation, isPaused ? InstallState.Paused : InstallState.Error);
                    await SaveLibraryAsync();
                    _activeCts.TryRemove(gameId, out _);

                    PathHelper.DeleteEmptyDirectory(stagingPath);
                    PathHelper.DeleteEmptyStagingRoot(stagingPath);

                    return downloadResult;
                }

                if (!installation.DownloadCompleted)
                {
                    installation.DownloadCompleted = true;
                    await SaveLibraryAsync().ConfigureAwait(false);
                }

                // --- Verify downloaded files (if checksums available) ---
                if (definition.Checksums.Files.Count > 0)
                {
                    SetState(installation, InstallState.Verifying);
                    var verResult = await _verifier.VerifyAsync(definition, stagingPath, trackedProgress, cts.Token).ConfigureAwait(false);
                    if (!verResult.Passed)
                    {
                        _logger.LogWarning("Verification failed after download for {GameId}", gameId);
                        // Continue anyway (warn only) - user can re-verify later
                    }
                }

                // --- Install phase ---
                var installSpace = _diskSpaceService.CalculateRequirement(definition, targetPath);

                // Staging files on the same drive will be deleted during installation —
                // count them as effectively free space when deciding whether to proceed.
                long stagingSizeBytes = await Task.Run(() =>
                {
                    try
                    {
                        return Directory.Exists(stagingPath)
                            ? Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
                                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                            : 0L;
                    }
                    catch { return 0L; }
                }, cts.Token).ConfigureAwait(false);

                var stagingDrive = Path.GetPathRoot(Path.GetFullPath(stagingPath));
                var installDrive = Path.GetPathRoot(Path.GetFullPath(targetPath));
                long effectiveAvailableBytes = installSpace.AvailableFreeSpaceBytes;
                if (string.Equals(stagingDrive, installDrive, StringComparison.OrdinalIgnoreCase))
                    effectiveAvailableBytes += stagingSizeBytes;

                if (effectiveAvailableBytes < installSpace.RequiredFreeSpaceBytes)
                {
                    long missingBytes = installSpace.RequiredFreeSpaceBytes - effectiveAvailableBytes;
                    var spaceMsg = string.Format(
                        Localizer.T("InstallInsufficientSpaceFormat",
                            "Недостаточно места на диске! Не хватает: {0}. Освободите место на {1} и попробуйте снова."),
                        PathHelper.FormatBytes(missingBytes),
                        installSpace.DriveRoot);
                    _logger.LogWarning("Insufficient disk space to install {GameId}: {Summary}", gameId, spaceMsg);
                    SetState(installation, InstallState.Paused);
                    await SaveLibraryAsync().ConfigureAwait(false);
                    _activeCts.TryRemove(gameId, out _);
                    return OperationResult.Fail(spaceMsg);
                }

                // Stop seeding BEFORE running the installer so the torrent engine releases
                // all file handles on the staging folder (setup.exe, archives, etc.).
                await _downloadManager.StopSeedingAsync(gameId).ConfigureAwait(false);

                SetState(installation, InstallState.Installing);
                await SaveLibraryAsync().ConfigureAwait(false);
                var installResult = await _installer.InstallFromStagingAsync(
                    gameId, stagingPath, installation.InstallPath, trackedProgress, cts.Token,
                    InstallOptions.FromDefinition(definition)).ConfigureAwait(false);

                if (!installResult.Success)
                {
                    installation.IsQueued = false;
                    installation.LastErrorMessage = installResult.ErrorMessage;
                    SetState(installation, InstallState.Error);
                    await SaveLibraryAsync();
                    _activeCts.TryRemove(gameId, out _);
                    return installResult;
                }

                if (!string.IsNullOrWhiteSpace(installResult.InstallPath))
                    installation.InstallPath = installResult.InstallPath;

                // Remove only this game's files; preserve staging for any other queued download.
                if (!PathHelper.DeleteGameStagingDirectory(stagingPath, gameId))
                    _logger.LogWarning("Could not remove staging for installed game {GameId}: {Path}", gameId, stagingPath);

                // --- Mark installed ---
                installation.InstalledVersion = definition.Version;
                installation.InstalledAt = DateTime.UtcNow;
                installation.InstalledSizeBytes = await Task.Run(() => GetDirectorySize(installation.InstallPath), cts.Token).ConfigureAwait(false);
                installation.PendingRelease = null;
                installation.WasDownloadingWhenClosed = false;
                installation.IsQueued = false;
                installation.LastErrorMessage = null;
                installation.CompletedInSession = true;
                ClearDownloadProgress(installation);
                SetState(installation, InstallState.Installed);
                await SaveLibraryAsync();

                _activeCts.TryRemove(gameId, out _);
                return OperationResult.Ok();
            }
            catch (OperationCanceledException)
            {
                installation.IsQueued = false;
                SetState(installation, InstallState.Paused);
                await SaveLibraryAsync();
                _activeCts.TryRemove(gameId, out _);
                return OperationResult.Fail("Installation paused.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Install failed for {GameId}", gameId);
                installation.IsQueued = false;
                installation.LastErrorMessage = $"Install failed: {ex.Message}";
                SetState(installation, InstallState.Error);
                await SaveLibraryAsync();
                _activeCts.TryRemove(gameId, out _);
                return OperationResult.Fail($"Install failed: {ex.Message}", ex);
            }
            finally
            {
                ReleaseDownloadSlot(gameId);
                _activeCts.TryRemove(gameId, out _);
                cts.Dispose();
                _activeOperations.TryRemove(gameId, out _);
                operationFinished.TrySetResult();
            }
        }

        public async Task<OperationResult> InstallReleaseAsync(
            GameReleaseCandidate release,
            string targetPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var gameId = !string.IsNullOrEmpty(release.Id)
                ? release.Id
                : release.CleanTitle.ToLowerInvariant().Replace(' ', '-').Replace(":", "");

            // Adapt release to a temporary GameDefinition
            var subfolder = PathHelper.SanitizeFileName(release.CleanTitle);
            var additionalTrackers = string.Equals(release.SourceId, "rutor", StringComparison.OrdinalIgnoreCase)
                ? new List<string>(RutorFallbackTrackers)
                : new List<string>();
            var definition = new GameDefinition
            {
                Id = gameId,
                Name = release.CleanTitle,
                Version = !string.IsNullOrEmpty(release.Version) ? release.Version : "1.0.0",
                InstallSubfolder = subfolder,
                DownloadSizeBytes = release.DownloadSizeBytes,
                SizeBytes = release.EstimatedInstalledSizeBytes,
                DownloadSources = new List<DownloadSource>()
            };

            if (!string.IsNullOrEmpty(release.TorrentFileUrl))
            {
                definition.DownloadSources.Add(new DownloadSource
                {
                    Type = "torrent",
                    Priority = 1,
                    TorrentFileUrl = release.TorrentFileUrl,
                    AdditionalTrackers = additionalTrackers,
                    InstallerMode = release.VersionType == GameVersionType.Repack ? "repack" : null
                });
            }

            if (!string.IsNullOrEmpty(release.MagnetLink))
            {
                definition.DownloadSources.Add(new DownloadSource
                {
                    Type = "torrent",
                    Priority = 2,
                    MagnetLink = release.MagnetLink,
                    AdditionalTrackers = additionalTrackers,
                    InstallerMode = release.VersionType == GameVersionType.Repack ? "repack" : null
                });
            }

            if (!string.IsNullOrEmpty(release.DirectUrl))
            {
                definition.DownloadSources.Add(new DownloadSource
                {
                    Type = "http",
                    Priority = 3,
                    Url = release.DirectUrl,
                    InstallerMode = release.VersionType == GameVersionType.Repack ? "repack" : null
                });
            }

            await WaitForPreviousOperationAsync(gameId, ct).ConfigureAwait(false);

            // A fresh user-initiated attempt supersedes an earlier removal tombstone.
            _removedDownloads.TryRemove(gameId, out _);

            var operationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_activeOperations.TryAdd(gameId, operationFinished))
                return OperationResult.Fail("This game is already being installed.");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts[gameId] = cts;
            _pausedReleases[gameId] = (release, targetPath);

            var installation = _installations.GetOrAdd(gameId, id => new GameInstallation
            {
                GameId = id,
                InstallPath = Path.Combine(targetPath, definition.InstallSubfolder),
            });

            installation.InstallPath = Path.Combine(targetPath, definition.InstallSubfolder);
            installation.DisplayName = release.CleanTitle;
            installation.PendingRelease = release;
            installation.TargetLibraryPath = targetPath;
            installation.WasDownloadingWhenClosed = false;
            installation.LastErrorMessage = null;
            installation.CompletedInSession = false;
            EnsureQueueOrder(installation);

            var trackedProgress = new ProgressRelay(installation, progress, this);

            // A resume re-enters with State=Paused or with partial bytes downloaded.
            // Distinguishing it lets a disk-space check only require space for the remaining download payload.
            var wasResuming = installation.State == InstallState.Paused || installation.DownloadedBytes > 0;
            var isTorrentRelease = !string.IsNullOrEmpty(release.MagnetLink)
                || !string.IsNullOrEmpty(release.TorrentFileUrl);

            // A fresh attempt starts a real download phase again; a resume keeps the
            // "payload already on disk" marker so Continue can jump straight to install.
            if (!wasResuming)
                installation.DownloadCompleted = false;

            try
            {
                // Perform the disk-space check BEFORE switching to Downloading so that
                // a failure on resume keeps the "Continue" button visible the whole time.
                // Previously SetState(Downloading) ran first, making IsPaused=false and
                // hiding the button while the error dialog was still open.
                trackedProgress.Report(new DownloadProgress
                {
                    GameId = gameId,
                    Phase = Localizer.T("DownloadPhaseCheckingDiskSpace", "Проверка места на диске…")
                });

                var spaceCheck = await Task.Run(
                    () => _diskSpaceService.CalculateRequirement(release, targetPath), cts.Token)
                    .ConfigureAwait(false);

                // During the download phase (and particularly when resuming an existing download),
                // we only require enough space to store the downloaded payload.
                // Full extraction space is only verified when the download completes and installation begins.
                var downloadPath = release.VersionType == GameVersionType.Portable
                    ? installation.InstallPath
                    : PathHelper.GetStagingPath(targetPath, gameId);
                // Torrent engines preallocate the whole payload when the torrent is added,
                // so a paused torrent can already occupy its final size on disk. Count what
                // is really there; otherwise a resume wrongly demands space for the full
                // remaining payload even though it is written into allocated files.
                var alreadyDownloaded = PathHelper.GetPayloadBytesOnDisk(
                    downloadPath, installation.DownloadedBytes);
                var hasSpaceForDownload = spaceCheck.HasEnoughSpaceForDownload(alreadyDownloaded);

                if (wasResuming)
                {
                    if (!hasSpaceForDownload)
                    {
                        var summary = spaceCheck.GetDownloadSummaryText(alreadyDownloaded);
                        _logger.LogWarning("Insufficient disk space to resume download for {Title}: {Summary}",
                            release.CleanTitle, summary);
                        installation.LastErrorMessage = summary;
                        InstallationStateChanged?.Invoke(this, installation);
                        return OperationResult.Fail(summary);
                    }
                }
                else
                {
                    if (!hasSpaceForDownload)
                    {
                        var summary = spaceCheck.GetDownloadSummaryText(alreadyDownloaded);
                        _logger.LogWarning("Insufficient disk space to start download for {Title}: {Summary}",
                            release.CleanTitle, summary);
                        installation.LastErrorMessage = summary;
                        SetState(installation, InstallState.Error);
                        await SaveLibraryAsync().ConfigureAwait(false);
                        _activeCts.TryRemove(gameId, out _);
                        _pausedReleases.TryRemove(gameId, out _);
                        return OperationResult.Fail(summary);
                    }
                }

                await AcquireDownloadSlotAsync(installation, trackedProgress, cts.Token).ConfigureAwait(false);

                SetState(installation, InstallState.Downloading);
                await SaveLibraryAsync().ConfigureAwait(false);

                PathHelper.EnsureDirectory(downloadPath);

                // Older versions treated a preallocated torrent file's full length as
                // proof of completion. If the saved piece progress is partial, that
                // marker is stale: clear it and let rqbit resume the existing task.
                if (isTorrentRelease
                    && installation.DownloadCompleted
                    && installation.DownloadWasTorrent
                    && installation.DownloadTotalBytes > 0
                    && installation.DownloadedBytes < installation.DownloadTotalBytes)
                {
                    _logger.LogWarning(
                        "Clearing stale completed-download marker for partial torrent {GameId}: {Downloaded}/{Total} bytes.",
                        gameId, installation.DownloadedBytes, installation.DownloadTotalBytes);
                    installation.DownloadCompleted = false;
                    await SaveLibraryAsync().ConfigureAwait(false);
                }

                // A torrent's file lengths cannot prove completion because rqbit
                // preallocates files before all pieces arrive. For torrents, only the
                // persisted completion marker set after a successful engine result counts.
                bool downloadAlreadyComplete = false;
                if (release.VersionType != GameVersionType.Portable
                    && installation.DownloadCompleted)
                {
                    // The download phase already finished on an earlier attempt (for
                    // example, the user paused while setup was extracting files). The
                    // archive may be partly consumed by the setup now, so the on-disk
                    // size is no longer a reliable signal — trust the persisted marker.
                    downloadAlreadyComplete = true;
                    _logger.LogInformation(
                        "Download for {GameId} was already completed in a previous attempt; skipping download phase.",
                        gameId);
                }
                else if (!isTorrentRelease
                    && release.DownloadSizeBytes > 0
                    && release.VersionType != GameVersionType.Portable)
                {
                    long stagingActualBytes = await Task.Run(() =>
                    {
                        try
                        {
                            return Directory.Exists(downloadPath)
                                ? Directory.EnumerateFiles(downloadPath, "*", SearchOption.AllDirectories)
                                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                                : 0L;
                        }
                        catch { return 0L; }
                    }, cts.Token).ConfigureAwait(false);

                    // Allow 1% tolerance for metadata/overhead files in the staging folder.
                    var threshold = (long)(release.DownloadSizeBytes * 0.99);
                    if (stagingActualBytes >= threshold)
                    {
                        downloadAlreadyComplete = true;
                        _logger.LogInformation(
                            "Staging folder for {GameId} already contains {Bytes} bytes (expected {Expected}); skipping download phase.",
                            gameId, stagingActualBytes, release.DownloadSizeBytes);
                    }
                }

                OperationResult downloadResult;
                if (downloadAlreadyComplete)
                {
                    downloadResult = OperationResult.Ok();
                }
                else
                {
                    downloadResult = await _downloadManager.DownloadAsync(
                        definition, downloadPath, trackedProgress, cts.Token).ConfigureAwait(false);
                }

                if (!downloadResult.Success)
                {
                    var paused = cts.IsCancellationRequested;
                    installation.IsQueued = false;
                    if (!paused)
                        installation.LastErrorMessage = downloadResult.ErrorMessage;
                    SetState(installation, paused ? InstallState.Paused : InstallState.Error);
                    await SaveLibraryAsync();
                    _activeCts.TryRemove(gameId, out _);

                    PathHelper.DeleteEmptyDirectory(downloadPath);
                    if (release.VersionType != GameVersionType.Portable)
                        PathHelper.DeleteEmptyStagingRoot(downloadPath);

                    return downloadResult;
                }

                // A torrent engine can report "finished" from stale fast-resume state while
                // the staging folder is actually empty (files cleaned up or moved). Running
                // the install phase in that case used to surface a misleading "not enough
                // disk space" error on Continue, because the staging credit was zero.
                // Validate the real payload first and keep the download resumable instead.
                if (isTorrentRelease
                    && !downloadAlreadyComplete
                    && release.VersionType != GameVersionType.Portable
                    && release.DownloadSizeBytes > 0)
                {
                    long payloadBytes = await Task.Run(
                        () => GetDirectorySize(downloadPath), cts.Token).ConfigureAwait(false);
                    if (payloadBytes < (long)(release.DownloadSizeBytes * 0.99))
                    {
                        _logger.LogWarning(
                            "Download for {Title} reported success but staging holds only {Actual} of {Expected} bytes; keeping it resumable.",
                            release.CleanTitle, payloadBytes, release.DownloadSizeBytes);
                        SetState(installation, InstallState.Paused);
                        await SaveLibraryAsync().ConfigureAwait(false);
                        return OperationResult.Fail(Localizer.T("TorrentErrorIncompleteVerify",
                            "Торрент завершился без подтверждения полной проверки данных."));
                    }
                }

                // The payload is fully on disk now. Persist that fact so pausing during
                // extraction (or a launcher restart) does not trigger a fresh download.
                if (!installation.DownloadCompleted)
                {
                    installation.DownloadCompleted = true;
                    await SaveLibraryAsync().ConfigureAwait(false);
                }

                if (release.VersionType != GameVersionType.Portable)
                {
                    var installSpace = await Task.Run(
                        () => _diskSpaceService.CalculateRequirement(release, targetPath), cts.Token)
                        .ConfigureAwait(false);

                    // The staging folder that was just downloaded lives on the same drive as the
                    // install target and will be deleted during installation. Count its current
                    // size as "virtually free" so that a valid install is not blocked by temp files.
                    long stagingSize = await Task.Run(() =>
                    {
                        try
                        {
                            return Directory.Exists(downloadPath)
                                ? Directory.EnumerateFiles(downloadPath, "*", SearchOption.AllDirectories)
                                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                                : 0L;
                        }
                        catch { return 0L; }
                    }, cts.Token).ConfigureAwait(false);

                    // Only credit the staging size when staging and install target share the same drive.
                    var stagingDriveRoot = Path.GetPathRoot(Path.GetFullPath(downloadPath));
                    var installDriveRoot = Path.GetPathRoot(Path.GetFullPath(targetPath));
                    long effectiveAvailable = installSpace.AvailableFreeSpaceBytes;
                    if (string.Equals(stagingDriveRoot, installDriveRoot, StringComparison.OrdinalIgnoreCase))
                        effectiveAvailable += stagingSize;

                    bool hasEnoughInstallSpace = effectiveAvailable >= installSpace.RequiredFreeSpaceBytes;
                    if (!hasEnoughInstallSpace)
                    {
                        long missing = installSpace.RequiredFreeSpaceBytes - effectiveAvailable;
                        var msg = string.Format(
                            Localizer.T("InstallInsufficientSpaceFormat",
                                "Недостаточно места на диске! Не хватает: {0}. Освободите место на {1} и попробуйте снова."),
                            PathHelper.FormatBytes(missing),
                            installSpace.DriveRoot);
                        _logger.LogWarning("Insufficient disk space to extract/install {Title}: {Summary}",
                            release.CleanTitle, msg);
                        SetState(installation, InstallState.Paused);
                        await SaveLibraryAsync().ConfigureAwait(false);
                        return OperationResult.Fail(msg);
                    }

                    // Stop seeding BEFORE running the installer so the torrent engine releases
                    // all file handles on the staging folder (setup.exe, archives, etc.).
                    await _downloadManager.StopSeedingAsync(gameId).ConfigureAwait(false);

                    SetState(installation, InstallState.Installing);
                    await SaveLibraryAsync().ConfigureAwait(false);
                    var installResult = await _installer.InstallFromStagingAsync(
                        gameId, downloadPath, installation.InstallPath, trackedProgress, cts.Token,
                        InstallOptions.FromDefinition(definition)).ConfigureAwait(false);

                    if (!installResult.Success)
                    {
                        installation.IsQueued = false;
                        installation.LastErrorMessage = installResult.ErrorMessage;
                        SetState(installation, InstallState.Error);
                        await SaveLibraryAsync();
                        _activeCts.TryRemove(gameId, out _);
                        return installResult;
                    }

                    if (!string.IsNullOrWhiteSpace(installResult.InstallPath))
                        installation.InstallPath = installResult.InstallPath;

                    if (!PathHelper.DeleteGameStagingDirectory(downloadPath, gameId))
                        _logger.LogWarning(
                            "Could not remove staging for installed release {GameId}: {Path}",
                            gameId, downloadPath);
                }

                installation.InstalledVersion = definition.Version;
                installation.InstalledAt = DateTime.UtcNow;
                installation.InstalledSizeBytes = await Task.Run(() => GetDirectorySize(installation.InstallPath), cts.Token).ConfigureAwait(false);
                installation.PendingRelease = null;
                installation.WasDownloadingWhenClosed = false;
                installation.IsQueued = false;
                installation.LastErrorMessage = null;
                installation.CompletedInSession = true;
                ClearDownloadProgress(installation);
                SetState(installation, InstallState.Installed);
                await SaveLibraryAsync();

                _activeCts.TryRemove(gameId, out _);
                _pausedReleases.TryRemove(gameId, out _);
                return OperationResult.Ok();
            }
            catch (OperationCanceledException)
            {
                installation.IsQueued = false;
                SetState(installation, InstallState.Paused);
                await SaveLibraryAsync();
                _activeCts.TryRemove(gameId, out _);
                return OperationResult.Fail("Installation paused.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Install failed for {GameId}", gameId);
                installation.IsQueued = false;
                installation.LastErrorMessage = $"Install failed: {ex.Message}";
                SetState(installation, InstallState.Error);
                await SaveLibraryAsync();
                _activeCts.TryRemove(gameId, out _);
                return OperationResult.Fail($"Install failed: {ex.Message}", ex);
            }
            finally
            {
                ReleaseDownloadSlot(gameId);
                _activeCts.TryRemove(gameId, out _);
                cts.Dispose();
                _activeOperations.TryRemove(gameId, out _);
                operationFinished.TrySetResult();
            }
        }

        public async Task PauseAsync(string gameId)
        {
            // Update the UI state immediately. Waiting for the engine can take a few
            // seconds, and the user should see "Continue" as soon as they press Pause.
            if (_installations.TryGetValue(gameId, out var installation))
            {
                installation.WasDownloadingWhenClosed = false;
                installation.IsQueued = false;
                if (installation.State is InstallState.Downloading
                    or InstallState.Verifying or InstallState.Installing)
                {
                    SetState(installation, InstallState.Paused);
                }

                await SaveLibraryAsync().ConfigureAwait(false);
            }

            ReleaseDownloadSlot(gameId);

            if (_activeCts.TryGetValue(gameId, out var cts))
            {
                // PauseRequested must be set before cancelling, otherwise the engine
                // reports a plain cancellation and forgets the partial download.
                await _downloadManager.PauseAsync(gameId).ConfigureAwait(false);

                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* operation already finished */ }
            }
        }

        public async Task<OperationResult> ResumeAsync(
            string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (!_installations.TryGetValue(gameId, out var installation))
                return OperationResult.Fail($"Game '{gameId}' is not tracked.");

            // If the previous attempt is still unwinding, give it a moment first.
            if (_activeOperations.ContainsKey(gameId)
                && installation.State is (InstallState.Downloading
                    or InstallState.Verifying or InstallState.Installing))
            {
                return OperationResult.Fail("This game is already being downloaded.");
            }

            await WaitForPreviousOperationAsync(gameId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            installation.WasDownloadingWhenClosed = false;
            installation.LastErrorMessage = null;

            OperationResult result;
            try
            {
                if (_pausedReleases.TryGetValue(gameId, out var pausedRelease))
                {
                    result = await InstallReleaseAsync(
                        pausedRelease.Release, pausedRelease.TargetPath, progress, ct).ConfigureAwait(false);
                    _logger.LogInformation("Release resume result for {GameId}: {Success}", gameId, result.Success);
                }
                else if (installation.PendingRelease != null)
                {
                    var targetPath = !string.IsNullOrWhiteSpace(installation.TargetLibraryPath)
                        ? installation.TargetLibraryPath
                        : Path.GetDirectoryName(installation.InstallPath) ?? _settings.Settings.DefaultInstallPath;
                    _pausedReleases[gameId] = (installation.PendingRelease, targetPath);
                    result = await InstallReleaseAsync(
                        installation.PendingRelease, targetPath, progress, ct).ConfigureAwait(false);
                    _logger.LogInformation("PendingRelease resume result for {GameId}: {Success}", gameId, result.Success);
                }
                else
                {
                    var targetPath = installation.TargetLibraryPath;
                    if (string.IsNullOrWhiteSpace(targetPath))
                        targetPath = Path.GetDirectoryName(installation.InstallPath);
                    if (string.IsNullOrWhiteSpace(targetPath))
                        targetPath = _settings.Settings.DefaultInstallPath;

                    // Re-start the install flow from the current state. The downloaders
                    // (torrent/http) resume from the files already present on disk.
                    result = await InstallAsync(gameId, targetPath!, progress, ct).ConfigureAwait(false);
                    _logger.LogInformation("Resume result for {GameId}: {Success}", gameId, result.Success);
                }
            }
            catch (OperationCanceledException)
            {
                result = OperationResult.Fail("Installation paused.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resume failed for {GameId}", gameId);
                result = OperationResult.Fail($"Resume failed: {ex.Message}", ex);
            }

            // A failed resume must not turn into a dead "Error" card: keep the partial
            // download resumable so the user can press Continue again.
            if (!result.Success
                && !ct.IsCancellationRequested
                && installation.State is not (InstallState.Installed or InstallState.UpdateAvailable))
            {
                installation.IsQueued = false;
                installation.LastErrorMessage = result.ErrorMessage;
                SetState(installation, InstallState.Paused);
                await SaveLibraryAsync().ConfigureAwait(false);
            }

            return result;
        }

        public async Task<OperationResult> RemoveDownloadAsync(string gameId)
        {
            // An update is applied in-place to an installed game and cannot be cancelled
            // safely. Removing the "Updating" card used to reset the game to NotInstalled
            // while its files stayed on disk (and the update kept running). Refuse it and
            // let the update finish instead.
            if (_installations.TryGetValue(gameId, out var updatingInstallation)
                && updatingInstallation.State == InstallState.Updating)
            {
                _logger.LogWarning("Refusing to remove {GameId}: an update is currently being applied.", gameId);
                return OperationResult.Fail("An update is currently being applied. Please wait for it to finish.");
            }

            try
            {
                ReleaseDownloadSlot(gameId);

                if (_activeCts.TryGetValue(gameId, out var activeCts))
                {
                    try { activeCts.Cancel(); }
                    catch (ObjectDisposedException) { /* operation already finished */ }
                }

                if (_activeOperations.TryGetValue(gameId, out var activeOperation))
                {
                    try { await activeOperation.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
                    catch (TimeoutException) { _logger.LogWarning("Timed out waiting for active operation {GameId} during remove.", gameId); }
                }

                await _downloadManager.CancelAsync(gameId).ConfigureAwait(false);
                _activeCts.TryRemove(gameId, out _);

                _installations.TryGetValue(gameId, out var currentInstallation);
                var libraryPath = !string.IsNullOrWhiteSpace(currentInstallation?.TargetLibraryPath)
                    ? currentInstallation!.TargetLibraryPath
                    : (currentInstallation != null ? Path.GetDirectoryName(currentInstallation.InstallPath) : null);
                if (string.IsNullOrWhiteSpace(libraryPath))
                    libraryPath = _settings.Settings.DefaultInstallPath;
                if (string.IsNullOrWhiteSpace(libraryPath))
                    libraryPath = PathHelper.DefaultLibraryPath;

                var isPortableRelease = (_pausedReleases.TryGetValue(gameId, out var releaseInfo)
                        && releaseInfo.Release.VersionType == GameVersionType.Portable)
                    || currentInstallation?.PendingRelease?.VersionType == GameVersionType.Portable;

                var downloadPath = isPortableRelease && !string.IsNullOrWhiteSpace(currentInstallation?.InstallPath)
                    ? currentInstallation!.InstallPath
                    : PathHelper.GetStagingPath(libraryPath, gameId);

                _pausedReleases.TryRemove(gameId, out _);

                // Mark the removal before resetting the state. Any state/progress write
                // from the operation that is still unwinding in the background is now
                // ignored, so a removed card can never come back as Paused/Installed.
                _removedDownloads[gameId] = 0;

                // Reset state to NotInstalled and persist BEFORE file deletion so a locked
                // file on disk never leaves a phantom entry in the Downloads list.
                if (currentInstallation != null
                    && currentInstallation.State != InstallState.Installed
                    && currentInstallation.State != InstallState.UpdateAvailable)
                {
                    currentInstallation.PendingRelease = null;
                    currentInstallation.WasDownloadingWhenClosed = false;
                    currentInstallation.IsQueued = false;
                    currentInstallation.LastErrorMessage = null;
                    currentInstallation.CompletedInSession = false;
                    ClearDownloadProgress(currentInstallation);
                    SetState(currentInstallation, InstallState.NotInstalled, force: true);
                    await SaveLibraryAsync().ConfigureAwait(false);
                }

                await Task.Run(() =>
                {
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            if (!Directory.Exists(downloadPath))
                                break;
                            foreach (var file in Directory.EnumerateFiles(downloadPath, "*", SearchOption.AllDirectories))
                            {
                                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                            }
                            Directory.Delete(downloadPath, recursive: true);
                            break;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            if (attempt == 4)
                                _logger.LogWarning(ex, "Could not delete partial download directory {Path}", downloadPath);
                            else
                                Thread.Sleep(150 * (attempt + 1));
                        }
                    }
                }).ConfigureAwait(false);

                if (!isPortableRelease)
                    PathHelper.DeleteEmptyStagingRoot(downloadPath);

                // If the installation target folder was created and is completely empty, clean it up
                if (!string.IsNullOrWhiteSpace(currentInstallation?.InstallPath)
                    && Directory.Exists(currentInstallation.InstallPath))
                {
                    PathHelper.DeleteEmptyDirectory(currentInstallation.InstallPath);
                }

                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not remove download {GameId}", gameId);
                return OperationResult.Fail($"Could not remove download: {ex.Message}", ex);
            }
        }

        public async Task<VerificationResult> VerifyAsync(
            string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var definition = await _catalog.GetGameAsync(gameId, ct);
            if (definition == null)
                return new VerificationResult { Passed = false, FailedFiles = { "Game not found in catalog" } };

            if (!_installations.TryGetValue(gameId, out var installation))
                return new VerificationResult { Passed = false, FailedFiles = { "Game not installed" } };

            _removedDownloads.TryRemove(gameId, out _);
            return await _verifier.VerifyAsync(definition, installation.InstallPath, progress, ct);
        }

        public async Task<OperationResult> UpdateAsync(
            string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var definition = await _catalog.GetGameAsync(gameId, ct);
            if (definition == null)
                return OperationResult.Fail($"Game '{gameId}' not found.");

            if (!_installations.TryGetValue(gameId, out var installation))
                return OperationResult.Fail("Game is not installed.");

            _removedDownloads.TryRemove(gameId, out _);

            var update = await _updater.CheckForUpdateAsync(definition, installation, ct);
            if (update == null)
                return OperationResult.Fail("No update available.");

            SetState(installation, InstallState.Updating);
            await SaveLibraryAsync();

            var result = await _updater.ApplyUpdateAsync(definition, installation, update, progress, ct);

            SetState(installation, result.Success ? InstallState.Installed : InstallState.Error);
            await SaveLibraryAsync();
            return result;
        }

        public async Task<OperationResult> UninstallAsync(string gameId)
        {
            if (!_installations.TryGetValue(gameId, out var installation))
                return OperationResult.Fail("Game not installed.");

            _removedDownloads.TryRemove(gameId, out _);
            SetState(installation, InstallState.Uninstalling);

            // Imported shortcuts point at user-managed game files. Removing the library entry
            // must never delete the executable's directory.
            var result = installation.IsExternalShortcut
                ? OperationResult.Ok()
                : await _installer.UninstallAsync(gameId, installation.InstallPath);
            if (result.Success)
            {
                _installations.TryRemove(gameId, out _);
            }
            else
            {
                SetState(installation, InstallState.Error);
            }

            await SaveLibraryAsync();
            return result;
        }

        public async Task CheckUpdatesAsync(CancellationToken ct = default)
        {
            var games = await _catalog.GetAllGamesAsync(ct).ConfigureAwait(false);
            foreach (var def in games)
            {
                if (!_installations.TryGetValue(def.Id, out var inst)) continue;
                if (inst.State != InstallState.Installed) continue;

                var update = await _updater.CheckForUpdateAsync(def, inst, ct);
                if (update != null)
                {
                    inst.UpdateAvailable = true;
                    inst.AvailableVersion = update.Version;
                    SetState(inst, InstallState.UpdateAvailable);
                }
            }
            await SaveLibraryAsync();
        }

        public async Task SaveLibraryAsync()
        {
            PathHelper.EnsureDirectory(PathHelper.AppDataDir);
            await _librarySaveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = JsonSerializer.Serialize(_installations.Values.ToList(), _jsonOptions);
                var libraryFile = PathHelper.LibraryFile;

                // Progress is persisted every few seconds during a download. Skip the
                // disk write when nothing actually changed.
                if (string.Equals(json, _lastSavedLibraryJson, StringComparison.Ordinal)
                    && File.Exists(libraryFile))
                    return;

                var tempFile = libraryFile + ".tmp";
                await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);

                // Replace is atomic and keeps exactly one previous generation in .bak,
                // so a crash can never leave a half-written library.json behind.
                if (File.Exists(libraryFile))
                {
                    try
                    {
                        File.Replace(tempFile, libraryFile, libraryFile + ".bak", ignoreMetadataErrors: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        File.Copy(tempFile, libraryFile, overwrite: true);
                        try { File.Delete(tempFile); } catch { }
                    }
                }
                else
                {
                    File.Move(tempFile, libraryFile);
                }

                _lastSavedLibraryJson = json;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save library.");
            }
            finally
            {
                _librarySaveLock.Release();
            }
        }

        public async Task LoadLibraryAsync()
        {
            if (!File.Exists(PathHelper.LibraryFile)) return;

            try
            {
                var json = await File.ReadAllTextAsync(PathHelper.LibraryFile);
                var list = JsonSerializer.Deserialize<List<GameInstallation>>(json, _jsonOptions) ?? new();
                _lastSavedLibraryJson = json;
                _installations.Clear();
                var executableByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var repairedPathBySavedPath = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var repaired = false;
                foreach (var item in list)
                {
                    if (item.PendingRelease != null)
                    {
                        var targetPath = !string.IsNullOrWhiteSpace(item.TargetLibraryPath)
                            ? item.TargetLibraryPath
                            : Path.GetDirectoryName(item.InstallPath) ?? _settings.Settings.DefaultInstallPath;
                        _pausedReleases[item.GameId] = (item.PendingRelease, targetPath);
                    }

                    if (item.State == InstallState.Downloading || item.IsQueued)
                    {
                        item.State = InstallState.Paused;
                        item.IsQueued = false;
                        item.WasDownloadingWhenClosed = true;
                        repaired = true;
                    }

                    if (item.QueueOrder > _nextQueueOrder)
                        _nextQueueOrder = item.QueueOrder;

                    if (!string.IsNullOrWhiteSpace(item.InstallPath)
                        && item.State is not (InstallState.Downloading or InstallState.Paused))
                    {
                        var savedPath = Path.GetFullPath(item.InstallPath);
                        var needsPathRepair = !Directory.Exists(savedPath)
                            || (item.State is (InstallState.Verifying or InstallState.Installing or InstallState.Updating)
                                && !HasGameExecutable(savedPath, executableByPath));
                        if (needsPathRepair)
                        {
                            if (!repairedPathBySavedPath.TryGetValue(savedPath, out var matchingDirectory))
                            {
                                matchingDirectory = FindMatchingInstallDirectory(savedPath);
                                repairedPathBySavedPath[savedPath] = matchingDirectory;
                            }

                            if (!string.IsNullOrWhiteSpace(matchingDirectory))
                            {
                                _logger.LogInformation("Corrected install path for {GameId} from {OldPath} to {NewPath}",
                                    item.GameId, item.InstallPath, matchingDirectory);
                                item.InstallPath = matchingDirectory;
                                repaired = true;
                            }
                        }
                    }

                    var installPathExists = Directory.Exists(item.InstallPath);
                    if ((item.State is InstallState.Installed or InstallState.UpdateAvailable
                            or InstallState.Updating or InstallState.Uninstalling)
                        && !installPathExists)
                    {
                        item.State = InstallState.NotInstalled;
                        item.UpdateAvailable = false;
                        repaired = true;
                        _logger.LogWarning("Install path missing for {GameId}: {Path}", item.GameId, item.InstallPath);
                    }
                    else if (installPathExists
                        && item.State is not (InstallState.Installed or InstallState.UpdateAvailable or InstallState.Paused or InstallState.Downloading))
                    {
                        var fullPath = Path.GetFullPath(item.InstallPath);
                        var hasGameExecutable = HasGameExecutable(fullPath, executableByPath);
                        var hasSavedInstallMetadata = item.InstalledAt != default
                            || item.InstalledSizeBytes > 0
                            || !string.IsNullOrWhiteSpace(item.InstalledVersion);
                        var wasInstalling = item.State is InstallState.Downloading or InstallState.Verifying or InstallState.Installing;

                        if (hasGameExecutable && (hasSavedInstallMetadata || wasInstalling))
                        {
                            item.State = item.UpdateAvailable ? InstallState.UpdateAvailable : InstallState.Installed;
                            if (item.InstalledAt == default)
                                item.InstalledAt = DateTime.UtcNow;
                            if (item.InstalledSizeBytes <= 0)
                                item.InstalledSizeBytes = GetDirectorySize(fullPath);
                            if (string.IsNullOrWhiteSpace(item.DisplayName))
                                item.DisplayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
                            repaired = true;
                            _logger.LogInformation("Recovered installed game {GameId} from {Path}", item.GameId, fullPath);
                        }
                    }

                    // An update interrupted by a crash/force-close must not leave a card
                    // permanently stuck in "Updating" (that state has no actions). The game
                    // files are still registered, so offer the update again instead.
                    if (item.State == InstallState.Updating)
                    {
                        item.State = InstallState.UpdateAvailable;
                        item.UpdateAvailable = true;
                        item.WasDownloadingWhenClosed = false;
                        repaired = true;
                        _logger.LogWarning("Recovered interrupted update for {GameId} at {Path}",
                            item.GameId, item.InstallPath);
                    }

                    // Likewise an interrupted uninstall: drop the dead "Uninstalling" state.
                    if (item.State == InstallState.Uninstalling)
                    {
                        item.State = InstallState.NotInstalled;
                        item.UpdateAvailable = false;
                        repaired = true;
                        _logger.LogWarning("Recovered interrupted uninstall for {GameId} at {Path}",
                            item.GameId, item.InstallPath);
                    }

                    if (installPathExists && string.IsNullOrWhiteSpace(item.DisplayName))
                    {
                        item.DisplayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.InstallPath));
                        repaired = true;
                    }

                    // A download/verify/install interrupted by closing (or crashing) the
                    // launcher must become resumable, otherwise the card stays stuck in a
                    // fake "Downloading/Installing" state with no working Continue button.
                    if (item.State is InstallState.Verifying or InstallState.Installing)
                    {
                        item.State = InstallState.Paused;
                        item.WasDownloadingWhenClosed = true;
                        repaired = true;
                    }

                    // If saved state is NotInstalled but there are partially-downloaded
                    // bytes (and a pending release to resume from), treat the entry as
                    // Paused so the Downloads tab shows the card with a working Continue
                    // button.  This can happen when the launcher crashes or is force-closed
                    // before PauseAsync has a chance to save the Paused state.
                    if (item.State == InstallState.NotInstalled
                        && item.DownloadedBytes > 0
                        && item.PendingRelease != null)
                    {
                        item.State = InstallState.Paused;
                        _logger.LogInformation(
                            "Restored Paused state for {GameId} with {Bytes} partially-downloaded bytes",
                            item.GameId, item.DownloadedBytes);
                        repaired = true;
                    }

                    // Stale download entries:
                    //  • An entry that duplicates a healthier installation of the SAME copy
                    //    (same install path, or same title in the same library folder) is
                    //    removed from the library entirely — the Downloads page must not show
                    //    the same game twice.
                    //  • An Error/NotInstalled entry that still has a resumable payload (release
                    //    info, recorded bytes or files on disk) is KEPT so the user can retry it
                    //    from the Downloads page. Wiping it would silently destroy the only
                    //    information needed to resume the download.
                    //  • Only an entry with neither files nor any recorded progress is dropped.
                    if (item.State is InstallState.Error or InstallState.NotInstalled)
                    {
                        var targetRoot = !string.IsNullOrWhiteSpace(item.TargetLibraryPath)
                            ? item.TargetLibraryPath
                            : (!string.IsNullOrWhiteSpace(item.InstallPath) ? Path.GetDirectoryName(item.InstallPath) : _settings.Settings.DefaultInstallPath);
                        var isPortable = item.PendingRelease?.VersionType == GameVersionType.Portable;
                        var expectedDownloadDir = isPortable
                            ? item.InstallPath
                            : (!string.IsNullOrWhiteSpace(targetRoot) ? PathHelper.GetStagingPath(targetRoot, item.GameId) : string.Empty);

                        var hasFilesOnDisk = !string.IsNullOrWhiteSpace(expectedDownloadDir)
                            && Directory.Exists(expectedDownloadDir)
                            && Directory.EnumerateFileSystemEntries(expectedDownloadDir).Any();

                        var hasResumableData = item.PendingRelease != null
                            || item.DownloadedBytes > 0
                            || item.DownloadTotalBytes > 0;

                        // A download pointed at a different library folder (e.g. another
                        // drive) is a separate copy with its own partial files. Keep it
                        // instead of silently dropping its resume metadata just because
                        // another copy of the same game is installed elsewhere.
                        var hasSuperiorSibling = list.Any(other =>
                            !ReferenceEquals(other, item)
                            && other.State is InstallState.Installed or InstallState.UpdateAvailable
                                or InstallState.Paused or InstallState.Downloading
                            && (PathsEqual(other.InstallPath, item.InstallPath)
                                || (string.Equals(other.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase)
                                    && PathsEqual(GetLibraryRootOf(other), targetRoot))));

                        if (hasSuperiorSibling)
                        {
                            _logger.LogInformation(
                                "Removing duplicate installation entry {GameId} ({DisplayName}) — a healthier entry already tracks it.",
                                item.GameId, item.DisplayName);
                            _pausedReleases.TryRemove(item.GameId, out _);
                            repaired = true;
                            continue;
                        }

                        if (!hasFilesOnDisk && !hasResumableData)
                        {
                            item.State = InstallState.NotInstalled;
                            item.PendingRelease = null;
                            item.LastErrorMessage = null;
                            ClearDownloadProgress(item);
                            _pausedReleases.TryRemove(item.GameId, out _);
                            repaired = true;
                        }
                    }

                    _installations[item.GameId] = item;
                }

                if (repaired)
                    await SaveLibraryAsync();

                _logger.LogInformation("Library loaded: {Count} game(s)", _installations.Count);

                // Clean up any staging folders left behind when the launcher was closed
                // mid-install (e.g. repack setup.exe was still running). Safe to run in
                // the background — only touches temp\<gameId> folders of installed games.
                _ = Task.Run(() => CleanupOrphanedStagingFolders());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load library.");
            }
        }

        public async Task ShutdownAndSaveAllDownloadsAsync()
        {
            var activeGameIds = _activeCts.Keys.ToList();
            if (activeGameIds.Count == 0)
                return;

            _logger.LogInformation("Shutting down and pausing {Count} active download(s)...", activeGameIds.Count);

            foreach (var gameId in activeGameIds)
            {
                if (_installations.TryGetValue(gameId, out var installation))
                {
                    installation.WasDownloadingWhenClosed = true;
                    SetState(installation, InstallState.Paused);
                }

                // Cancel our own token before unwinding so the operation reports a pause
                // (not a failure). Otherwise closing the launcher could persist Error and
                // the next start would show an unusable card instead of Continue.
                if (_activeCts.TryGetValue(gameId, out var activeCts))
                {
                    try { activeCts.Cancel(); }
                    catch (ObjectDisposedException) { /* operation already finished */ }
                }

                try
                {
                    await _downloadManager.PauseAndSaveAsync(gameId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanly pause download {GameId}", gameId);
                }
            }

            try
            {
                await SaveLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save library during download shutdown.");
            }
        }

        public IReadOnlyList<string> GetPendingAutoResumeGameIds()
        {
            return _installations.Values
                .Where(i => i.WasDownloadingWhenClosed && i.State == InstallState.Paused)
                .OrderBy(i => i.QueueOrder > 0 ? i.QueueOrder : int.MaxValue)
                .Select(i => i.GameId)
                .ToList();
        }

        private void EnsureQueueOrder(GameInstallation installation)
        {
            if (installation.QueueOrder <= 0)
                installation.QueueOrder = Interlocked.Increment(ref _nextQueueOrder);
        }

        private async Task AcquireDownloadSlotAsync(
            GameInstallation installation,
            IProgress<DownloadProgress>? progress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            QueuedDownloadWaiter? waiter = null;
            lock (_queueLock)
            {
                var maxConcurrent = Math.Max(1, _settings.Settings.MaxConcurrentDownloads);
                if (_activeDownloadSlotIds.Contains(installation.GameId)
                    || _activeDownloadSlotIds.Count < maxConcurrent)
                {
                    _activeDownloadSlotIds.Add(installation.GameId);
                    installation.IsQueued = false;
                    return;
                }

                installation.IsQueued = true;
                waiter = new QueuedDownloadWaiter
                {
                    GameId = installation.GameId,
                    Installation = installation
                };
                _waitingQueue.Add(waiter);
            }

            SetState(installation, InstallState.Downloading);
            await SaveLibraryAsync().ConfigureAwait(false);
            progress?.Report(new DownloadProgress
            {
                GameId = installation.GameId,
                Phase = Localizer.T("StatusQueued", "В очереди")
            });

            using var reg = ct.Register(() =>
            {
                lock (_queueLock)
                {
                    _waitingQueue.Remove(waiter);
                }
                waiter.SlotGranted.TrySetCanceled(ct);
            });

            await waiter.SlotGranted.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        private void ReleaseDownloadSlot(string gameId)
        {
            List<QueuedDownloadWaiter>? toWake = null;
            lock (_queueLock)
            {
                _activeDownloadSlotIds.Remove(gameId);
                _waitingQueue.RemoveAll(w => string.Equals(w.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                if (_installations.TryGetValue(gameId, out var inst))
                    inst.IsQueued = false;

                var maxConcurrent = Math.Max(1, _settings.Settings.MaxConcurrentDownloads);
                while (_activeDownloadSlotIds.Count < maxConcurrent && _waitingQueue.Count > 0)
                {
                    _waitingQueue.Sort((a, b) => a.Installation.QueueOrder.CompareTo(b.Installation.QueueOrder));
                    var next = _waitingQueue[0];
                    _waitingQueue.RemoveAt(0);
                    _activeDownloadSlotIds.Add(next.GameId);
                    next.Installation.IsQueued = false;
                    toWake ??= new List<QueuedDownloadWaiter>();
                    toWake.Add(next);
                }
            }

            if (toWake != null)
            {
                foreach (var w in toWake)
                {
                    SetState(w.Installation, InstallState.Downloading);
                    w.SlotGranted.TrySetResult(true);
                }
            }
        }

        public void NotifyMaxConcurrentDownloadsChanged()
        {
            List<QueuedDownloadWaiter>? toWake = null;
            List<string>? toPause = null;
            lock (_queueLock)
            {
                var maxConcurrent = Math.Max(1, _settings.Settings.MaxConcurrentDownloads);
                while (_activeDownloadSlotIds.Count < maxConcurrent && _waitingQueue.Count > 0)
                {
                    _waitingQueue.Sort((a, b) => a.Installation.QueueOrder.CompareTo(b.Installation.QueueOrder));
                    var next = _waitingQueue[0];
                    _waitingQueue.RemoveAt(0);
                    _activeDownloadSlotIds.Add(next.GameId);
                    next.Installation.IsQueued = false;
                    toWake ??= new List<QueuedDownloadWaiter>();
                    toWake.Add(next);
                }

                // Lowering the limit must also trim already-running downloads, otherwise
                // the setting silently has no effect until the next restart.
                if (_activeDownloadSlotIds.Count > maxConcurrent)
                {
                    toPause = _activeDownloadSlotIds
                        .Select(id => _installations.TryGetValue(id, out var inst) ? inst : null)
                        .Where(inst => inst != null
                            && !inst.IsQueued
                            && inst.State == InstallState.Downloading)
                        .OrderByDescending(inst => inst!.QueueOrder)
                        .Take(_activeDownloadSlotIds.Count - maxConcurrent)
                        .Select(inst => inst!.GameId)
                        .ToList();
                }
            }

            if (toWake != null)
            {
                foreach (var w in toWake)
                {
                    SetState(w.Installation, InstallState.Downloading);
                    w.SlotGranted.TrySetResult(true);
                }
            }

            if (toPause != null)
            {
                foreach (var gameId in toPause)
                {
                    _logger.LogInformation(
                        "MaxConcurrentDownloads lowered — pausing {GameId} to respect the new limit.", gameId);
                    _ = PauseAsync(gameId);
                }
            }
        }

        public void MoveDownloadInQueue(string gameId, int delta)
        {
            if (delta == 0 || !_installations.TryGetValue(gameId, out var target))
                return;

            List<GameInstallation> ordered;
            lock (_queueLock)
            {
                ordered = _installations.Values
                    .Where(i => i.State is InstallState.Downloading or InstallState.Paused or InstallState.Error
                                || i.IsQueued)
                    .OrderBy(i => i.QueueOrder > 0 ? i.QueueOrder : int.MaxValue)
                    .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (int i = 0; i < ordered.Count; i++)
                    ordered[i].QueueOrder = i + 1;

                var idx = ordered.FindIndex(i => string.Equals(i.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                    return;

                var swapIdx = Math.Clamp(idx + (delta < 0 ? -1 : 1), 0, ordered.Count - 1);
                if (swapIdx == idx)
                    return;

                (ordered[idx].QueueOrder, ordered[swapIdx].QueueOrder) =
                    (ordered[swapIdx].QueueOrder, ordered[idx].QueueOrder);
            }

            foreach (var item in ordered)
                InstallationStateChanged?.Invoke(this, item);
            _ = SaveLibraryAsync();
        }

        public void PromoteDownloadToFront(string gameId)
        {
            if (!_installations.TryGetValue(gameId, out var target))
                return;

            string? activeToPause = null;
            lock (_queueLock)
            {
                var minOrder = _installations.Values
                    .Where(i => i.QueueOrder > 0)
                    .Select(i => i.QueueOrder)
                    .DefaultIfEmpty(1)
                    .Min();
                target.QueueOrder = Math.Max(1, minOrder - 1);

                // Normalize queue orders
                var ordered = _installations.Values
                    .Where(i => i.QueueOrder > 0)
                    .OrderBy(i => string.Equals(i.GameId, gameId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(i => i.QueueOrder)
                    .ToList();
                for (int i = 0; i < ordered.Count; i++)
                    ordered[i].QueueOrder = i + 1;

                // If target is currently waiting in the queue and all slots are busy,
                // pause the lowest-priority active download so target starts immediately.
                var isWaiting = _waitingQueue.Any(w => string.Equals(w.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                var maxConcurrent = Math.Max(1, _settings.Settings.MaxConcurrentDownloads);
                if (isWaiting && _activeDownloadSlotIds.Count >= maxConcurrent)
                {
                    activeToPause = _activeDownloadSlotIds
                        .Select(id => _installations.TryGetValue(id, out var inst) ? inst : null)
                        .Where(inst => inst != null && inst.State == InstallState.Downloading && !inst.IsQueued)
                        .OrderByDescending(inst => inst!.QueueOrder)
                        .Select(inst => inst!.GameId)
                        .FirstOrDefault();
                }
            }

            InstallationStateChanged?.Invoke(this, target);
            _ = SaveLibraryAsync();

            if (!string.IsNullOrEmpty(activeToPause))
            {
                _ = PauseAsync(activeToPause);
            }
        }

        public void DismissCompletedDownload(string gameId)
        {
            if (_installations.TryGetValue(gameId, out var installation) && installation.CompletedInSession)
            {
                installation.CompletedInSession = false;
                InstallationStateChanged?.Invoke(this, installation);
            }
        }

        public void ClearCompletedDownloads()
        {
            foreach (var installation in _installations.Values.Where(i => i.CompletedInSession).ToList())
            {
                installation.CompletedInSession = false;
                InstallationStateChanged?.Invoke(this, installation);
            }
        }

        private async Task WaitForPreviousOperationAsync(string gameId, CancellationToken ct)
        {
            if (!_activeOperations.TryGetValue(gameId, out var previous))
                return;

            try
            {
                await previous.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Previous operation for {GameId} did not finish in time.", gameId);
            }
        }

        private void MaybePersistProgress()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastProgressSaveTicks);
            if (now - last < 5000)
                return;
            if (Interlocked.CompareExchange(ref _lastProgressSaveTicks, now, last) != last)
                return;

            _ = SaveLibraryAsync();
        }

        private static void ClearDownloadProgress(GameInstallation installation)
        {
            installation.DownloadedBytes = 0;
            if (!installation.CompletedInSession)
                installation.DownloadTotalBytes = 0;
            installation.DownloadWasTorrent = false;
            installation.DownloadCompleted = false;
        }

        /// <summary>
        /// Forwards progress to the caller while keeping the last snapshot on the
        /// installation record, so a paused/closed download still shows its progress
        /// (and a sensible percentage) after the launcher is opened again.
        /// </summary>
        private sealed class ProgressRelay : IProgress<DownloadProgress>
        {
            private readonly GameInstallation _installation;
            private readonly IProgress<DownloadProgress>? _inner;
            private readonly GameManager _owner;

            public ProgressRelay(
                GameInstallation installation,
                IProgress<DownloadProgress>? inner,
                GameManager owner)
            {
                _installation = installation;
                _inner = inner;
                _owner = owner;
            }

            public void Report(DownloadProgress value)
            {
                if (value == null)
                    return;

                // The download was removed; ignore late progress from the unwinding
                // operation so it cannot repopulate progress/persisted bytes.
                if (_owner._removedDownloads.ContainsKey(_installation.GameId))
                    return;

                // Never overwrite a non-zero persisted progress with 0 from a
                // phase-only report (e.g. "Запуск...", "Проверка места…").
                // BytesDownloaded defaults to 0 on phase-only messages, so
                // checking > 0 prevents wiping the saved resume position which
                // would cause the disk-space guard to demand full double-space
                // and the UI to show "0 MB / total" when resuming.
                if (value.BytesDownloaded > 0)
                    _installation.DownloadedBytes = value.BytesDownloaded;
                if (value.TotalBytes > 0)
                    _installation.DownloadTotalBytes = value.TotalBytes;
                else if (_installation.DownloadTotalBytes <= 0 && _installation.PendingRelease?.DownloadSizeBytes > 0)
                    _installation.DownloadTotalBytes = _installation.PendingRelease.DownloadSizeBytes;
                if (value.IsTorrent.HasValue)
                    _installation.DownloadWasTorrent = value.IsTorrent.Value;

                // Preserve non-zero persisted progress on phase-only reports during
                // download/preparation so the UI progress bar never jumps back to 0%.
                if (_installation.State == InstallState.Downloading)
                {
                    if (value.BytesDownloaded <= 0 && _installation.DownloadedBytes > 0)
                        value.BytesDownloaded = _installation.DownloadedBytes;
                    if (value.TotalBytes <= 0 && _installation.DownloadTotalBytes > 0)
                        value.TotalBytes = _installation.DownloadTotalBytes;
                    if (value.Fraction <= 0 && value.BytesDownloaded > 0 && value.TotalBytes > 0)
                        value.Fraction = Math.Clamp((double)value.BytesDownloaded / value.TotalBytes, 0.0, 1.0);
                }

                _owner.MaybePersistProgress();
                _inner?.Report(value);
            }
        }

        /// <summary>
        /// Deletes leftover staging folders (temp\&lt;gameId&gt;) and empty abandoned install
        /// directories. Called on startup so repack temp files and ghost folders are cleaned up.
        /// </summary>
        private void CleanupOrphanedStagingFolders()
        {
            var activeOrPausedGameIds = new HashSet<string>(
                _installations.Values
                    .Where(i => i.State is InstallState.Downloading or InstallState.Paused or InstallState.Verifying or InstallState.Installing)
                    .Select(i => PathHelper.SanitizeFileName(i.GameId)),
                StringComparer.OrdinalIgnoreCase);

            var libraryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_settings.Settings.DefaultInstallPath) && Directory.Exists(_settings.Settings.DefaultInstallPath))
                libraryRoots.Add(Path.GetFullPath(_settings.Settings.DefaultInstallPath));

            foreach (var installation in _installations.Values)
            {
                if (!string.IsNullOrWhiteSpace(installation.InstallPath))
                {
                    var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installation.InstallPath));
                    if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                        libraryRoots.Add(Path.GetFullPath(root));
                }
                if (!string.IsNullOrWhiteSpace(installation.TargetLibraryPath) && Directory.Exists(installation.TargetLibraryPath))
                    libraryRoots.Add(Path.GetFullPath(installation.TargetLibraryPath));
            }

            foreach (var root in libraryRoots)
            {
                try
                {
                    var tempDir = Path.Combine(root, "temp");
                    if (!Directory.Exists(tempDir))
                        continue;

                    var subdirs = Directory.GetDirectories(tempDir);
                    foreach (var subdir in subdirs)
                    {
                        var dirName = Path.GetFileName(subdir);
                        var isActivelyTracked = activeOrPausedGameIds.Contains(dirName);
                        if (!isActivelyTracked)
                        {
                            _logger.LogInformation("Cleaning up orphaned staging folder: {Path}", subdir);
                            PathHelper.DeleteGameStagingDirectory(subdir, dirName);
                        }
                        else
                        {
                            PathHelper.DeleteEmptyDirectory(subdir);
                        }
                    }

                    PathHelper.DeleteEmptyStagingRoot(tempDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error cleaning up staging directory in {Root}", root);
                }
            }

            foreach (var installation in _installations.Values)
            {
                if (installation.State == InstallState.NotInstalled
                    && !string.IsNullOrWhiteSpace(installation.InstallPath)
                    && Directory.Exists(installation.InstallPath))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(installation.InstallPath).Any())
                        {
                            _logger.LogInformation("Cleaning up empty install folder for NotInstalled game {GameId}: {Path}",
                                installation.GameId, installation.InstallPath);
                            Directory.Delete(installation.InstallPath, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete empty install folder {Path}", installation.InstallPath);
                    }
                }
            }
        }

        private static bool HasGameExecutable(string path, IDictionary<string, bool> cache)
        {
            if (!Directory.Exists(path))
                return false;

            var fullPath = Path.GetFullPath(path);
            if (!cache.TryGetValue(fullPath, out var hasExecutable))
            {
                hasExecutable = GameExecutableLocator.FindMainExecutable(fullPath) != null;
                cache[fullPath] = hasExecutable;
            }

            return hasExecutable;
        }

        private static string? FindMatchingInstallDirectory(string savedPath)
        {
            try
            {
                var fullSavedPath = Path.GetFullPath(savedPath);
                var parentDirectory = Path.GetDirectoryName(fullSavedPath);
                var expectedName = NormalizeDirectoryName(Path.GetFileName(fullSavedPath));
                if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory) || expectedName.Length == 0)
                    return null;

                var matches = Directory.EnumerateDirectories(parentDirectory)
                    .Where(path => !string.Equals(Path.GetFullPath(path), fullSavedPath, StringComparison.OrdinalIgnoreCase))
                    .Where(path => string.Equals(
                        NormalizeDirectoryName(Path.GetFileName(path)), expectedName, StringComparison.Ordinal))
                    .Where(path => GameExecutableLocator.FindMainExecutable(path) != null)
                    .Take(2)
                    .ToList();

                return matches.Count == 1 ? matches[0] : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string NormalizeDirectoryName(string name)
            => new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        /// <summary>Case-insensitive, full-path comparison that tolerates empty values.</summary>
        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Library root a persisted entry belongs to (explicit target or install parent).</summary>
        private static string? GetLibraryRootOf(GameInstallation installation)
        {
            if (!string.IsNullOrWhiteSpace(installation.TargetLibraryPath))
                return installation.TargetLibraryPath;

            if (string.IsNullOrWhiteSpace(installation.InstallPath))
                return null;

            return Path.GetDirectoryName(installation.InstallPath);
        }

        private void SetState(GameInstallation installation, InstallState state, bool force = false)
        {
            // A download that was removed while its operation was still unwinding must
            // not be resurrected by that operation's late state changes.
            if (!force && _removedDownloads.ContainsKey(installation.GameId))
                return;

            installation.State = state;
            InstallationStateChanged?.Invoke(this, installation);
        }

        private static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0;
            long size = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch (FileNotFoundException) { }
            }
            return size;
        }
    }
}
