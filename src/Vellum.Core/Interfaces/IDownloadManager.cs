using System;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    /// <summary>
    /// Manages one or more concurrent downloads.
    /// Picks the appropriate downloader (torrent vs HTTP) based on source type.
    /// </summary>
    public interface IDownloadManager
    {
        /// <summary>Download a game using its best available source.</summary>
        Task<OperationResult> DownloadAsync(
            GameDefinition game,
            string destinationFolder,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Pause a running download.</summary>
        Task PauseAsync(string gameId);

        /// <summary>Resume a paused download.</summary>
        Task ResumeAsync(string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Pause a running download and wait for fast-resume data to be persisted.</summary>
        Task PauseAndSaveAsync(string gameId);

        /// <summary>
        /// Stop seeding a completed torrent (forget it, keeping the downloaded files).
        /// No-op when the game has no live torrent.
        /// </summary>
        Task StopSeedingAsync(string gameId);

        /// <summary>Cancel and clean up a download.</summary>
        Task CancelAsync(string gameId);
    }
}
