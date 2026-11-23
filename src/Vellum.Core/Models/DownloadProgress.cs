using System;

namespace Vellum.Core.Models
{
    /// <summary>
    /// Real-time download progress snapshot for a specific game.
    /// Raised via IProgress&lt;DownloadProgress&gt; or events.
    /// </summary>
    public class DownloadProgress
    {
        public string GameId { get; set; } = string.Empty;

        /// <summary>0.0 – 1.0</summary>
        public double Fraction { get; set; }

        /// <summary>0 – 100</summary>
        public double Percent => Math.Round(Fraction * 100, 1);

        /// <summary>Bytes downloaded so far.</summary>
        public long BytesDownloaded { get; set; }

        /// <summary>Total bytes expected.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Current download speed in bytes/sec.</summary>
        public long SpeedBytesPerSec { get; set; }

        /// <summary>Estimated seconds remaining (-1 if unknown).</summary>
        public long SecondsRemaining { get; set; } = -1;

        /// <summary>Name of the file or phase currently being processed.</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>Human-readable phase (e.g. "Downloading", "Verifying", "Extracting").</summary>
        public string Phase { get; set; } = "Downloading";

        /// <summary>
        /// Language-independent phase identifier (see <see cref="DownloadPhases"/>).
        /// UI state must use this instead of matching <see cref="Phase"/> text.
        /// </summary>
        public string? PhaseKey { get; set; }

        /// <summary>Whether this progress snapshot belongs to a torrent source.</summary>
        public bool? IsTorrent { get; set; }

        /// <summary>Upload speed for torrent seeding (bytes/sec).</summary>
        public long UploadSpeedBytesPerSec { get; set; }

        /// <summary>Number of peers connected (torrent).</summary>
        public int Peers { get; set; }

        /// <summary>Total number of known peers in swarm (torrent).</summary>
        public int TotalPeers { get; set; }

        /// <summary>Number of seeders if reported by source or tracker.</summary>
        public int Seeders { get; set; }

        /// <summary>True when a download has had zero progress/peers for an extended period.</summary>
        public bool IsStalled { get; set; }
    }

    /// <summary>
    /// Result of a download or installation operation.
    /// </summary>
    public class OperationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public Exception? Exception { get; set; }
        public string? InstallPath { get; set; }

        public static OperationResult Ok(string? installPath = null)
            => new() { Success = true, InstallPath = installPath };
        public static OperationResult Fail(string msg, Exception? ex = null)
            => new() { Success = false, ErrorMessage = msg, Exception = ex };
    }
}
