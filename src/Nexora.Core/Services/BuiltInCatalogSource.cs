using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Game source that exposes games from the built-in and configured catalog (games.json).
    /// </summary>
    public class BuiltInCatalogSource : IGameSource
    {
        private readonly IGameCatalog _catalog;

        public string SourceId => "catalog";
        public string DisplayName => "Nexora Catalog";
        public int Priority { get; set; } = 0; // Highest default priority
        public bool IsEnabled { get; set; } = true;

        public BuiltInCatalogSource(IGameCatalog catalog)
        {
            _catalog = catalog;
        }

        public async Task<IReadOnlyList<GameReleaseCandidate>> SearchAsync(string query, CancellationToken ct = default)
        {
            var list = new List<GameReleaseCandidate>();
            var allGames = await _catalog.GetAllGamesAsync(ct);

            foreach (var game in allGames)
            {
                if (string.IsNullOrWhiteSpace(query) ||
                    game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    game.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    game.Categories.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)))
                {
                    // Check if game definition specifies a version or download source
                    var primarySource = game.DownloadSources.OrderBy(s => s.Priority).FirstOrDefault();
                    var candidate = new GameReleaseCandidate
                    {
                        Id = game.Id,
                        RawTitle = $"{game.Name} [v {game.Version}]",
                        CleanTitle = game.Name,
                        SourceId = SourceId,
                        SourceName = DisplayName,
                        SourcePriority = Priority,
                        VersionType = GameVersionType.Portable, // Built-in test games are portable zip / direct
                        Version = game.Version,
                        ReleaseAuthor = game.Developer,
                        DownloadSizeBytes = game.DownloadSizeBytes > 0 ? game.DownloadSizeBytes : game.SizeBytes,
                        EstimatedInstalledSizeBytes = game.SizeBytes > 0 ? game.SizeBytes : game.DownloadSizeBytes,
                        MagnetLink = primarySource?.MagnetLink,
                        TorrentFileUrl = primarySource?.TorrentFileUrl,
                        DirectUrl = primarySource?.Url,
                        Seeders = 100, // Local / CDN source always available
                        Leechers = 0,
                        PublishDate = DateTime.UtcNow,
                        SteamAppId = game.SteamAppId
                    };

                    list.Add(candidate);
                }
            }

            return list;
        }

        public Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default)
        {
            return SearchAsync(string.Empty, ct);
        }
    }
}
