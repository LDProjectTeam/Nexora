using System;
using System.Collections.Generic;

namespace Vellum.Core.Models
{
    /// <summary>
    /// User-visible update status for one installed game. Maps one-to-one to the
    /// statuses required by the launcher UI.
    /// </summary>
    public enum GameUpdatePhase
    {
        Unknown,
        UpToDate,
        UpdateAvailable,
        Checking,
        WaitingForDownload,
        Downloading,
        Paused,
        Verifying,
        Installing,
        Error
    }

    public enum GameUpdateErrorKind
    {
        None,
        Check,
        Download,
        Install
    }

    public class GameUpdateStatus
    {
        public string GameId { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string? AvailableVersion { get; set; }
        public string CurrentBuildId { get; set; } = string.Empty;
        public string? AvailableBuildId { get; set; }
        public GameUpdatePhase Phase { get; set; } = GameUpdatePhase.Unknown;

        public long UpdateSizeBytes { get; set; }
        public int ChangedFileCount { get; set; }
        public int AddedFileCount { get; set; }
        public int DeletedFileCount { get; set; }

        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public long SpeedBytesPerSec { get; set; }
        public long SecondsRemaining { get; set; } = -1;

        public DateTimeOffset? LastCheckedUtc { get; set; }
        public string? ErrorMessage { get; set; }
        public GameUpdateErrorKind ErrorKind { get; set; } = GameUpdateErrorKind.None;

        public bool IsBusy => Phase is GameUpdatePhase.Checking
            or GameUpdatePhase.Downloading
            or GameUpdatePhase.Verifying
            or GameUpdatePhase.Installing
            or GameUpdatePhase.WaitingForDownload;

        public bool HasUpdate => Phase is GameUpdatePhase.UpdateAvailable
            or GameUpdatePhase.WaitingForDownload
            or GameUpdatePhase.Downloading
            or GameUpdatePhase.Paused
            or GameUpdatePhase.Verifying
            or GameUpdatePhase.Installing
            || (Phase == GameUpdatePhase.Error && !string.IsNullOrWhiteSpace(AvailableVersion));

        public double Fraction => TotalBytes > 0
            ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0.0, 1.0)
            : 0.0;

        public GameUpdateStatus Clone() => (GameUpdateStatus)MemberwiseClone();
    }

    public class GameUpdateCheckResult
    {
        public string GameId { get; set; } = string.Empty;
        public bool SourceConfigured { get; set; }
        public bool UpdateAvailable { get; set; }
        public UpdateManifest? Manifest { get; set; }
        public FileDiffResult? Diff { get; set; }
        public GameUpdateStatus Status { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    public enum FileChangeKind
    {
        Unchanged,
        Modified,
        Added,
        Deleted,
        Corrupted
    }

    public class FileChange
    {
        public string Path { get; set; } = string.Empty;
        public FileChangeKind Kind { get; set; }

        /// <summary>Size in the new manifest (0 for deleted files).</summary>
        public long Size { get; set; }

        public string Sha256 { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;

        public bool RequiresDownload => Kind is FileChangeKind.Added
            or FileChangeKind.Modified
            or FileChangeKind.Corrupted;
    }

    public class FileDiffResult
    {
        public string GameId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string BuildId { get; set; } = string.Empty;
        public UpdateManifest? Manifest { get; set; }
        public List<FileChange> Changes { get; set; } = new();

        public long BytesToDownload { get; set; }
        public long TotalManifestBytes { get; set; }
        public int UnchangedFileCount { get; set; }

        public int AddedFileCount => Count(FileChangeKind.Added);
        public int ModifiedFileCount => Count(FileChangeKind.Modified);
        public int CorruptedFileCount => Count(FileChangeKind.Corrupted);
        public int DeletedFileCount => Count(FileChangeKind.Deleted);

        public int ChangedFileCount => AddedFileCount + ModifiedFileCount + CorruptedFileCount;

        public bool IsUpToDate => BytesToDownload == 0 && DeletedFileCount == 0 && ChangedFileCount == 0;

        public IEnumerable<FileChange> Downloads
        {
            get
            {
                foreach (var change in Changes)
                {
                    if (change.RequiresDownload)
                        yield return change;
                }
            }
        }

        private int Count(FileChangeKind kind)
        {
            var count = 0;
            foreach (var change in Changes)
            {
                if (change.Kind == kind)
                    count++;
            }
            return count;
        }
    }

    /// <summary>A game tracked by the persistent update queue.</summary>
    public class GameUpdateQueueEntry
    {
        public string GameId { get; set; } = string.Empty;
        public int Order { get; set; }
        public bool UserInitiated { get; set; }
    }

    public class InstalledFileRecord
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    /// <summary>Persistent per-game update record. Serialized to updates.json.</summary>
    public class GameUpdateRecord
    {
        public string GameId { get; set; } = string.Empty;
        public string? InstallPath { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string BuildId { get; set; } = string.Empty;
        public string Channel { get; set; } = "stable";

        public string? PendingManifestUrl { get; set; }
        public string? PendingManifestPath { get; set; }
        public string? PendingVersion { get; set; }
        public string? PendingBuildId { get; set; }
        public GameUpdatePhase Phase { get; set; } = GameUpdatePhase.Unknown;

        public long UpdateSizeBytes { get; set; }
        public int ChangedFileCount { get; set; }
        public int AddedFileCount { get; set; }
        public int DeletedFileCount { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }

        public DateTimeOffset? LastCheckedUtc { get; set; }
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public string? ErrorMessage { get; set; }
        public GameUpdateErrorKind ErrorKind { get; set; } = GameUpdateErrorKind.None;

        public bool DownloadCompleted { get; set; }
        public int QueueOrder { get; set; }
        public bool IsQueued { get; set; }

        /// <summary>Manifest that describes the currently installed build, used to detect deletions.</summary>
        public List<InstalledFileRecord> InstalledFiles { get; set; } = new();

        public bool RollbackAvailable { get; set; }
    }

    public class UpdatePersistedState
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, GameUpdateRecord> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<GameUpdateQueueEntry> Queue { get; set; } = new();

        /// <summary>True when the queue was running and should continue after restart.</summary>
        public bool QueueWasRunning { get; set; }

        public LauncherUpdatePersistedState? Launcher { get; set; }

        public List<UpdateErrorRecord> ErrorHistory { get; set; } = new();
    }

    public class UpdateErrorRecord
    {
        public string GameId { get; set; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
        public string Message { get; set; } = string.Empty;
    }

    public class LauncherUpdatePersistedState
    {
        public string? OfferedVersion { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? LastCheckedUtc { get; set; }
        public bool DownloadReady { get; set; }
        public string? StagingDir { get; set; }

        public string? BootstrapperAttemptVersion { get; set; }
        public int BootstrapperAttempts { get; set; }
        public DateTimeOffset? BootstrapperAttemptUtc { get; set; }
    }

    /// <summary>
    /// State written after a successful launcher package apply. It is the signed
    /// manifest that describes the files currently installed, so the bootstrapper can
    /// verify them before every launch and after every update.
    /// </summary>
    public class LauncherInstalledState
    {
        public int SchemaVersion { get; set; } = 1;
        public string Version { get; set; } = string.Empty;
        public DateTimeOffset InstalledUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<ManifestFile> Files { get; set; } = new();
        public List<string> DeletedFiles { get; set; } = new();
    }

    public enum LauncherUpdatePhase
    {
        Unknown,
        UpToDate,
        Checking,
        UpdateAvailable,
        Downloading,
        Verifying,
        ReadyToInstall,
        Applying,
        Error
    }

    public class LauncherUpdateStatus
    {
        public string CurrentVersion { get; set; } = string.Empty;
        public string? AvailableVersion { get; set; }
        public LauncherUpdatePhase Phase { get; set; } = LauncherUpdatePhase.Unknown;
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public long SpeedBytesPerSec { get; set; }
        public long SecondsRemaining { get; set; } = -1;
        public double Fraction => TotalBytes > 0
            ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0.0, 1.0)
            : 0.0;
        public string? ErrorMessage { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? StagingDir { get; set; }

        public LauncherUpdateStatus Clone() => (LauncherUpdateStatus)MemberwiseClone();
    }

    /// <summary>Result of verifying the integrity of a local install or a staged payload.</summary>
    public class ManifestVerificationResult
    {
        public bool Passed { get; set; }
        public List<FileChange> Problems { get; set; } = new();
        public long VerifiedBytes { get; set; }
    }

    public class UpdateDownloadResult
    {
        public bool Success { get; set; }
        public bool Paused { get; set; }
        public string? ErrorMessage { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }

        public static UpdateDownloadResult Ok(long downloaded, long total)
            => new() { Success = true, DownloadedBytes = downloaded, TotalBytes = total };

        public static UpdateDownloadResult PausedAt(long downloaded, long total)
            => new() { Paused = true, DownloadedBytes = downloaded, TotalBytes = total };

        public static UpdateDownloadResult Fail(string message, long downloaded, long total)
            => new() { Success = false, ErrorMessage = message, DownloadedBytes = downloaded, TotalBytes = total };
    }
}
