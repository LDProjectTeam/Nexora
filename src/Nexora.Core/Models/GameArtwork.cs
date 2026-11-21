namespace Nexora.Core.Models
{
    public sealed class GameArtwork
    {
        public string CoverPath { get; init; } = string.Empty;
        public string BannerPath { get; init; } = string.Empty;
        public string OfficialReleaseDate { get; init; } = string.Empty;

        /// <summary>
        /// Title of the store/catalog entry the artwork was taken from. Cached entries whose
        /// title does not belong to the requested game are ignored instead of being shown.
        /// </summary>
        public string MatchedTitle { get; init; } = string.Empty;
    }
}
