using System;

namespace Nexora.Core.Models
{
    /// <summary>
    /// Represents a specific release version of a game found in an external source.
    /// </summary>
    public class GameReleaseCandidate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Raw release title as posted in the source.</summary>
        public string RawTitle { get; set; } = string.Empty;

        /// <summary>Normalized / canonical title of the game (e.g. "The Witcher 3: Wild Hunt").</summary>
        public string CleanTitle { get; set; } = string.Empty;

        /// <summary>Alternative or localized title (e.g. "Ведьмак 3: Дикая Охота").</summary>
        public string AltTitle { get; set; } = string.Empty;

        /// <summary>Identifier of the source.</summary>
        public string SourceId { get; set; } = string.Empty;

        /// <summary>Display name of the source.</summary>
        public string SourceName { get; set; } = string.Empty;

        /// <summary>Priority of this source (lower number = higher priority).</summary>
        public int SourcePriority { get; set; } = 1;

        /// <summary>Version type: Portable, Repack, Installer, Archive.</summary>
        public GameVersionType VersionType { get; set; } = GameVersionType.Unknown;

        /// <summary>Version string if detected (e.g. "1.32", "2.13").</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Release group / author (e.g. "InsaneRamZes", "FitGirl", "GOG", "DODI").</summary>
        public string ReleaseAuthor { get; set; } = string.Empty;

        /// <summary>Download size in bytes.</summary>
        public long DownloadSizeBytes { get; set; }

        /// <summary>Estimated size on disk after complete installation/extraction in bytes.</summary>
        public long EstimatedInstalledSizeBytes { get; set; }

        /// <summary>Magnet link for peer-to-peer download.</summary>
        public string? MagnetLink { get; set; }

        /// <summary>Direct link to .torrent file if available.</summary>
        public string? TorrentFileUrl { get; set; }

        /// <summary>Direct HTTP/HTTPS download link if available.</summary>
        public string? DirectUrl { get; set; }

        /// <summary>URL to release page on the tracker / website.</summary>
        public string? DetailsUrl { get; set; }

        /// <summary>Number of active seeders (peers with 100% of files).</summary>
        public int Seeders { get; set; }

        /// <summary>Number of active leechers.</summary>
        public int Leechers { get; set; }

        /// <summary>Release publication date.</summary>
        public DateTime? PublishDate { get; set; }

        /// <summary>Release language or voice/text details.</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>
        /// Official Steam AppID when the source exposes one. Used as a strong hint for
        /// Steam metadata and still validated against the title and release year.
        /// </summary>
        public int? SteamAppId { get; set; }

        public bool IsPortable => VersionType == GameVersionType.Portable;

        public override string ToString()
        {
            var typeTag = VersionType switch
            {
                GameVersionType.Portable => "[PORTABLE]",
                GameVersionType.Installer => "[INSTALLER]",
                GameVersionType.Repack => "[REPACK]",
                GameVersionType.Archive => "[ARCHIVE]",
                _ => ""
            };
            return $"{typeTag} {CleanTitle} ({SourceName}) - {Helpers.PathHelper.FormatBytes(DownloadSizeBytes)}";
        }
    }
}
