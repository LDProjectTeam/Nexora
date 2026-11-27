using System;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    /// <summary>Downloads and validates signed manifests. Implementations may cache or use any transport.</summary>
    public interface IUpdateManifestProvider
    {
        /// <summary>Fetch a game manifest from an absolute URL.</summary>
        Task<UpdateManifest> GetGameManifestAsync(string gameId, string manifestUrl,
            CancellationToken ct = default);

        /// <summary>Fetch the launcher self-update manifest.</summary>
        Task<LauncherUpdateManifest> GetLauncherManifestAsync(string manifestUrl,
            CancellationToken ct = default);
    }

    /// <summary>Verifies RSA signatures of manifest payloads.</summary>
    public interface IManifestSignatureVerifier
    {
        /// <summary>True when at least one trusted public key is configured.</summary>
        bool HasTrustedKeys { get; }

        /// <summary>Verify a signature over raw payload bytes.</summary>
        bool Verify(ReadOnlySpan<byte> payload, string signatureBase64, string? keyId, out string? error);
    }

    /// <summary>Compares an installed game tree with a remote manifest.</summary>
    public interface IFileDiffService
    {
        Task<FileDiffResult> ComputeAsync(
            UpdateManifest manifest,
            string installRoot,
            IReadOnlyList<InstalledFileRecord>? previousManifest,
            bool verifyUnchangedFiles,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);
    }

    /// <summary>Hashes and validates individual files and complete manifests.</summary>
    public interface IIntegrityVerifier
    {
        Task<bool> VerifyFileAsync(string filePath, long expectedSize, string expectedSha256,
            CancellationToken ct = default);

        Task<ManifestVerificationResult> VerifyRootAsync(
            UpdateManifest manifest,
            string root,
            bool requireAllFiles,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);
    }

    /// <summary>Downloads the changed files of a game update into a staging directory.</summary>
    public interface IUpdateDownloadManager
    {
        Task<UpdateDownloadResult> DownloadAsync(
            GameUpdatePlan plan,
            string stagingRoot,
            long maxSpeedBps,
            int maxConcurrentFiles,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);
    }

    /// <summary>Safe transactional installation of a staged update.</summary>
    public interface IUpdateInstaller
    {
        Task<OperationResult> ApplyAsync(
            GameUpdatePlan plan,
            string stagingRoot,
            UpdatePersistedState state,
            Func<Task>? beforeApply = null,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        Task<RollbackOutcome> RollbackAsync(string gameId, string installRoot, CancellationToken ct = default);

        /// <summary>Recovers transactions left behind by a crash/force-close. Returns recovered game ids.</summary>
        Task<IReadOnlyList<string>> RecoverAsync(CancellationToken ct = default);
    }

    /// <summary>Persists update queue, per-game records and rollback history atomically.</summary>
    public interface IUpdateStateStore
    {
        UpdatePersistedState State { get; }
        Task LoadAsync(CancellationToken ct = default);
        Task SaveAsync(CancellationToken ct = default);
        Task SaveManifestAsync(string gameId, UpdateManifest manifest, CancellationToken ct = default);
        Task<UpdateManifest?> LoadManifestAsync(string gameId, CancellationToken ct = default);
        Task DeleteManifestAsync(string gameId);

        /// <summary>Appends a bounded, user-visible error history entry.</summary>
        void RecordError(string gameId, string message);

        /// <summary>Acquires an exclusive process-wide and cross-process lock for a game update.</summary>
        Task<IDisposable?> TryAcquireGameLockAsync(string gameId, CancellationToken ct = default);
    }

    /// <summary>Detects, queues, downloads and installs game updates.</summary>
    public interface IGameUpdateService
    {
        event EventHandler<GameUpdateStatus>? StatusChanged;

        IReadOnlyCollection<GameUpdateStatus> Statuses { get; }

        GameUpdateStatus GetStatus(string gameId);

        Task InitializeAsync(CancellationToken ct = default);

        Task<GameUpdateCheckResult> CheckForUpdateAsync(
            GameDefinition definition,
            GameInstallation installation,
            bool force = false,
            CancellationToken ct = default);

        Task CheckAllAsync(
            IEnumerable<GameDefinition> definitions,
            CancellationToken ct = default);

        bool HasPendingUpdate(string gameId);

        Task<OperationResult> StartUpdateAsync(
            string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        Task<OperationResult> ResumeUpdateAsync(
            string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        Task PauseAsync(string gameId);

        Task RemoveAsync(string gameId);

        Task UpdateAllAsync(CancellationToken ct = default);

        Task PauseAllAsync();

        Task ResumeAllAsync();

        void MoveInQueue(string gameId, int delta);

        void Promote(string gameId);

        void NotifyConcurrencyChanged();

        Task ShutdownAsync();

        Task<RollbackOutcome> RollbackAsync(string gameId, CancellationToken ct = default);
    }

    /// <summary>Launcher self-update: check, download, verify, stage and hand off to the bootstrapper.</summary>
    public interface ILauncherUpdateService
    {
        event EventHandler<LauncherUpdateStatus>? StatusChanged;

        LauncherUpdateStatus Status { get; }

        LauncherUpdateManifest? AvailableManifest { get; }

        Task<LauncherUpdateStatus> CheckForUpdatesAsync(string currentVersion,
            bool autoDownload = true, CancellationToken ct = default);

        Task<OperationResult> DownloadAsync(string currentVersion, CancellationToken ct = default);

        Task<OperationResult> DownloadBootstrapperAsync(BootstrapperUpdateInfo info, string destinationPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);

        LauncherApplyRequest CreateApplyRequest(string installDir, string entryPoint, string backupDir,
            string resultFile, int waitProcessId, bool relaunch);

        Task<OperationResult> PrepareRestartAsync(int currentProcessId, string currentVersion,
            CancellationToken ct = default);

        /// <summary>Cleans caches and reports the result of an update applied by the bootstrapper.</summary>
        Task<LauncherUpdateResultRecord?> ConsumeBootstrapperResultAsync(CancellationToken ct = default);
    }

    /// <summary>Description of one update operation used by the downloader and installer.</summary>
    public class GameUpdatePlan
    {
        public UpdateManifest Manifest { get; set; } = new();
        public FileDiffResult Diff { get; set; } = new();
        public string GameId => Manifest.GameId;
        public string InstallRoot { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ManifestUrl { get; set; } = string.Empty;
        public IReadOnlyList<InstalledFileRecord> PreviousManifest { get; set; } = Array.Empty<InstalledFileRecord>();
    }

    public class RollbackOutcome
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RestoredVersion { get; set; }
    }

    public class ProcessStartResult
    {
        public bool Started { get; set; }
        public int? ProcessId { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>Starts the main launcher or a replacement process. Tests use a fake.</summary>
    public interface IProcessStarter
    {
        ProcessStartResult Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? standardInput = null);
    }

    /// <summary>Verifies the files required to launch the main application.</summary>
    public interface ILauncherIntegrityService
    {
        Task<LauncherIntegrityResult> VerifyAsync(string appDirectory, string launcherExecutableName,
            CancellationToken ct = default);

        Task<LauncherInstalledState?> ReadInstalledStateAsync(CancellationToken ct = default);
    }

    /// <summary>Runs the mandatory pre-launch sequence: pending recovery, updates, integrity, launch.</summary>
    public interface IBootstrapperWorkflow
    {
        Task<BootstrapperRunResult> RunAsync(BootstrapperRunRequest request,
            IProgress<BootstrapperProgress>? progress = null,
            CancellationToken ct = default);
    }

    /// <summary>Replaces a running bootstrapper executable after the old process exits.</summary>
    public interface IBootstrapperSelfUpdater
    {
        Task<BootstrapperSelfUpdateResult> ApplyAsync(BootstrapperSelfUpdateRequest request,
            IProgress<BootstrapperProgress>? progress = null,
            CancellationToken ct = default);
    }
}
