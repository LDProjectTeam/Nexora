using System;
using System.Collections.Generic;

namespace Vellum.Core.Models
{
    /// <summary>
    /// Represents a game entry as defined in the game catalog (games.json).
    /// This is the static definition of a game — what it is, where to get it.
    /// </summary>
    public class GameDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Developer { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public List<string> Categories { get; set; } = new();
        public List<string> Tags { get; set; } = new();

        /// <summary>Path or URL to the cover image.</summary>
        public string CoverUrl { get; set; } = string.Empty;

        /// <summary>Wide hero artwork URL or cached path for the featured game banner.</summary>
        public string BannerUrl { get; set; } = string.Empty;

        /// <summary>Path or URL to the icon.</summary>
        public string IconUrl { get; set; } = string.Empty;

        public List<string> Screenshots { get; set; } = new();
        public string ReleaseDate { get; set; } = string.Empty;

        /// <summary>
        /// Official Steam AppID from catalog data, when known. Optional; the details
        /// window still verifies the store page name and release year.
        /// </summary>
        public int? SteamAppId { get; set; }

        /// <summary>Total installed size in bytes.</summary>
        public long SizeBytes { get; set; }

        /// <summary>Download size in bytes (may be smaller if compressed).</summary>
        public long DownloadSizeBytes { get; set; }

        /// <summary>Relative path to the main executable inside install folder.</summary>
        public string Executable { get; set; } = string.Empty;

        public List<string> LaunchArguments { get; set; } = new();

        /// <summary>Subfolder name within the default install directory.</summary>
        public string InstallSubfolder { get; set; } = string.Empty;

        /// <summary>Ordered list of download sources (tried by priority).</summary>
        public List<DownloadSource> DownloadSources { get; set; } = new();

        /// <summary>Available updates for this game.</summary>
        public List<GameUpdate> Updates { get; set; } = new();

        /// <summary>System dependencies required (e.g. DirectX, VC++ Redist).</summary>
        public List<string> Dependencies { get; set; } = new();

        public FileChecksums Checksums { get; set; } = new();
    }

    public class DownloadSource
    {
        /// <summary>Source type: "torrent", "http", "manifest", "installer"</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Lower number = higher priority.</summary>
        public int Priority { get; set; } = 1;

        // Torrent fields
        public string? MagnetLink { get; set; }
        public string? TorrentFileUrl { get; set; }

        /// <summary>Extra public trackers supplied by a public release source.</summary>
        public List<string> AdditionalTrackers { get; set; } = new();

        // HTTP fields
        public string? Url { get; set; }

        /// <summary>Archive type: "zip", "7z", "tar.gz", "none"</summary>
        public string? ArchiveType { get; set; }

        // Manifest-based (file list)
        public string? ManifestUrl { get; set; }

        // Installer
        public string? InstallerArgs { get; set; }

        /// <summary>"auto" (default) or "interactive": how to run a setup.exe found in the download.</summary>
        public string? InstallerMode { get; set; }
    }

    public class GameUpdate
    {
        public string Version { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string ManifestUrl { get; set; } = string.Empty;
        public List<DownloadSource> Sources { get; set; } = new();
    }

    public class FileChecksums
    {
        public string Algorithm { get; set; } = "SHA256";

        /// <summary>Map of relative file path → expected hash.</summary>
        public Dictionary<string, string> Files { get; set; } = new();
    }
}
