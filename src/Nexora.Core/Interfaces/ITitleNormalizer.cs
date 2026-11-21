using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    public class NormalizedTitleInfo
    {
        public string CleanTitle { get; set; } = string.Empty;
        public string AltTitle { get; set; } = string.Empty;
        public GameVersionType VersionType { get; set; } = GameVersionType.Unknown;
        public string Version { get; set; } = string.Empty;
        public string ReleaseAuthor { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parses and normalizes messy release titles from torrent trackers and web catalogs,
    /// extracting canonical game name, alternate titles, version type (Portable, Repack, Installer), etc.
    /// </summary>
    public interface ITitleNormalizer
    {
        NormalizedTitleInfo Normalize(string rawTitle);

        /// <summary>Checks whether two titles refer to the same game.</summary>
        bool AreTitlesMatching(string titleA, string titleB);
    }
}
