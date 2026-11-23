using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    /// <summary>
    /// Central game management service. Tracks installed games and orchestrates
    /// install/update/uninstall operations.
    /// </summary>
    public interface IGameManager
    {
        /// <summary>Raised whenever an installation or download changes state.</summary>
        event EventHandler<GameInstallation>? InstallationStateChanged;

        /// <summary>All known installations (including NotInstalled state).</summary>
        IReadOnlyDictionary<string, GameInstallation> Installations { get; }

        /// <summary>Get installation record for a game (always returns a record, State=NotInstalled if not found).</summary>
        GameInstallation GetInstallation(string gameId);

        /// <summary>Tracks or updates an installation record.</summary>
        void TrackInstallation(GameInstallation installation);

        /// <summary>Start installing a game to the given path.</summary>
        Task<OperationResult> InstallAsync(string gameId, string targetPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Start installing a specific release candidate (Portable, Repack, etc.) to the given path.</summary>
        Task<OperationResult> InstallReleaseAsync(GameReleaseCandidate release, string targetPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Pause an active download.</summary>
        Task PauseAsync(string gameId);

        /// <summary>Resume a paused download.</summary>
        Task<OperationResult> ResumeAsync(string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Remove a paused or active download and delete its temporary data.</summary>
        Task<OperationResult> RemoveDownloadAsync(string gameId);

        /// <summary>Verify all files of an installed game.</summary>
        Task<VerificationResult> VerifyAsync(string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Update a game to a newer version.</summary>
        Task<OperationResult> UpdateAsync(string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Uninstall a game and remove its files.</summary>
        Task<OperationResult> UninstallAsync(string gameId);

        /// <summary>Check if a newer version is available for installed games.</summary>
        Task CheckUpdatesAsync(CancellationToken ct = default);

        /// <summary>Persist installation state to library.json.</summary>
        Task SaveLibraryAsync();

        /// <summary>Load installation state from library.json.</summary>
        Task LoadLibraryAsync();

        /// <summary>Gracefully pauses and saves fast resume data for all currently running downloads before launcher exit.</summary>
        Task ShutdownAndSaveAllDownloadsAsync();

        /// <summary>Returns the IDs of games that were in progress when the launcher was previously closed.</summary>
        /// <summary>Moves a download up (delta &lt; 0) or down (delta &gt; 0) in the queue order.</summary>
        void MoveDownloadInQueue(string gameId, int delta);

        /// <summary>Promotes a queued download to the front of the queue.</summary>
        void PromoteDownloadToFront(string gameId);

        /// <summary>Re-evaluates the download queue when MaxConcurrentDownloads changes.</summary>
        void NotifyMaxConcurrentDownloadsChanged();

        /// <summary>Dismisses a completed download from the session's completed list without deleting files.</summary>
        void DismissCompletedDownload(string gameId);

        /// <summary>Clears all completed downloads from the session's completed list without deleting files.</summary>
        void ClearCompletedDownloads();
    }
}
