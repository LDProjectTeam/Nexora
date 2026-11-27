using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vellum.Core.Models
{
    /// <summary>
    /// Represents the installed state of a game on this machine.
    /// Persisted in library.json in AppData.
    /// </summary>
    public class GameInstallation
    {
        public string GameId { get; set; } = string.Empty;

        /// <summary>Display name for games that are no longer present in the catalog.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Full path to the folder where the game is installed.</summary>
        public string InstallPath { get; set; } = string.Empty;

        /// <summary>Installed version string.</summary>
        public string InstalledVersion { get; set; } = string.Empty;

        public InstallState State { get; set; } = InstallState.NotInstalled;

        public DateTime InstalledAt { get; set; }
        public DateTime LastPlayedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }

        /// <summary>Total time played in seconds.</summary>
        public long TotalPlaySeconds { get; set; }

        /// <summary>Actual size on disk in bytes (measured after install).</summary>
        public long InstalledSizeBytes { get; set; }

        /// <summary>Whether a newer version is available.</summary>
        public bool UpdateAvailable { get; set; }

        /// <summary>Version string of available update, if any.</summary>
        public string? AvailableVersion { get; set; }

        // --- Manifest-based update state (managed by GameUpdateService) ---

        /// <summary>Build id of the installed build.</summary>
        public string InstalledBuildId { get; set; } = string.Empty;

        /// <summary>Build id of the available update.</summary>
        public string? AvailableBuildId { get; set; }

        /// <summary>Bytes that must be downloaded for the available update.</summary>
        public long UpdateDownloadSizeBytes { get; set; }

        /// <summary>Number of files changed by the available update.</summary>
        public int UpdateChangedFiles { get; set; }

        /// <summary>Number of new files added by the available update.</summary>
        public int UpdateAddedFiles { get; set; }

        /// <summary>Number of files removed by the available update.</summary>
        public int UpdateDeletedFiles { get; set; }

        /// <summary>When the update source was last checked for this game.</summary>
        public DateTime UpdateCheckedAtUtc { get; set; }

        /// <summary>Update channel (stable/beta) associated with the pending update.</summary>
        public string? UpdateChannel { get; set; }

        /// <summary>Manifest URL used for the pending update; persisted so a paused update can resume.</summary>
        public string? PendingManifestUrl { get; set; }

        /// <summary>True when a previous build can be restored from the rollback backup.</summary>
        public bool RollbackAvailable { get; set; }

        /// <summary>Release information saved for downloading or paused downloads so they can resume across launcher restarts.</summary>
        public GameReleaseCandidate? PendingRelease { get; set; }

        /// <summary>Target folder where the game is being installed.</summary>
        public string? TargetLibraryPath { get; set; }

        /// <summary>External executable represented by an imported Windows shortcut.</summary>
        public bool IsExternalShortcut { get; set; }
        public string ExternalExecutablePath { get; set; } = string.Empty;
        public string ExternalLaunchArguments { get; set; } = string.Empty;
        public string ExternalWorkingDirectory { get; set; } = string.Empty;

        /// <summary>Whether this download was actively downloading when the launcher was closed.</summary>
        public bool WasDownloadingWhenClosed { get; set; }

        /// <summary>Last known amount of verified/downloaded bytes (persisted so the card can show progress after a restart).</summary>
        public long DownloadedBytes { get; set; }

        /// <summary>Last known total download size in bytes.</summary>
        public long DownloadTotalBytes { get; set; }

        /// <summary>Whether the last download used the torrent engine.</summary>
        public bool DownloadWasTorrent { get; set; }

        /// <summary>
        /// True once the download phase finished and the full payload is on disk.
        /// Persisted so pausing during installation, or restarting the launcher, can
        /// go straight to install instead of re-downloading everything.
        /// </summary>
        public bool DownloadCompleted { get; set; }

        /// <summary>
        /// True while the download is waiting in the queue for an available concurrency slot.
        /// </summary>
        public bool IsQueued { get; set; }

        /// <summary>
        /// Position in the download queue (lower value = higher priority).
        /// </summary>
        public int QueueOrder { get; set; }

        /// <summary>
        /// Last error message recorded when a download or installation fails.
        /// </summary>
        public string? LastErrorMessage { get; set; }

        /// <summary>
        /// True when the download finished during the current launcher session and has not been dismissed from the Downloads list.
        /// </summary>
        [JsonIgnore]
        public bool CompletedInSession { get; set; }
    }

    public enum InstallState
    {
        NotInstalled,
        Downloading,
        Paused,
        Verifying,
        Installing,
        Installed,
        UpdateAvailable,
        Updating,
        Uninstalling,
        Error
    }
}
