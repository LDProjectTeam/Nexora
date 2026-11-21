using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Common abstraction for torrent download engines (Rust librqbit).
    /// </summary>
    public interface ITorrentDownloader : IDisposable
    {
        /// <summary>
        /// Start or resume downloading a torrent (from a magnet link or .torrent URL/file).
        /// </summary>
        Task<OperationResult> DownloadAsync(
            string gameId,
            string magnetOrTorrentUrl,
            bool isTorrentFileUrl,
            string destinationFolder,
            IProgress<DownloadProgress>? progress = null,
            long maxDownloadSpeedBps = 0,
            long maxUploadSpeedBps = 0,
            CancellationToken ct = default,
            IEnumerable<string>? additionalTrackers = null);

        /// <summary>
        /// Pause active download for the specified game.
        /// </summary>
        Task PauseAsync(string gameId);

        /// <summary>
        /// Stop download and remove from active session, keeping downloaded files.
        /// </summary>
        Task StopAsync(string gameId);

        /// <summary>
        /// Remove download from session.
        /// </summary>
        Task RemoveAsync(string gameId);

        /// <summary>
        /// Pause download and persist resume state.
        /// </summary>
        Task PauseAndSaveResumeAsync(string gameId);
    }
}
