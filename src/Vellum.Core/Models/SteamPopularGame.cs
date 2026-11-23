namespace Vellum.Core.Models
{
    public sealed class SteamPopularGame
    {
        public SteamPopularGame(uint appId, string name, string logoUrl = "")
        {
            AppId = appId;
            Name = name;
            LogoUrl = logoUrl;
        }

        public uint AppId { get; }
        public string Name { get; }
        public string LogoUrl { get; }
        public string CoverUrl => $"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/header.jpg";
        public string BannerUrl => $"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/library_hero.jpg";
    }

    public sealed class SteamFeaturedGame
    {
        public SteamFeaturedGame(SteamPopularGame steamGame, GameReleaseCandidate release)
        {
            SteamGame = steamGame;
            Release = release;
        }

        public SteamPopularGame SteamGame { get; }
        public GameReleaseCandidate Release { get; }
    }
}
