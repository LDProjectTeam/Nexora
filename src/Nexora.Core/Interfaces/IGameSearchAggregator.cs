using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Searches across all configured game sources,
    /// matches game names across sources, and selects the best version.
    /// </summary>
    public interface IGameSearchAggregator
    {
        /// <summary>All currently registered game sources.</summary>
        IReadOnlyList<IGameSource> Sources { get; }

        /// <summary>Add or register a game source.</summary>
        void RegisterSource(IGameSource source);

        /// <summary>Search all enabled sources concurrently for a game query.</summary>
        Task<IReadOnlyList<GameReleaseCandidate>> SearchAllSourcesAsync(string query, CancellationToken ct = default);

        /// <summary>
        /// Search for a game, aggregate versions across all sources, match titles,
        /// and pick the best version (Portable preferred) with disk space requirement computed.
        /// </summary>
        Task<VersionSelectionResult> FindAndSelectBestVersionAsync(
            string gameTitle,
            string targetPath,
            CancellationToken ct = default);

        /// <summary>Calculate install-space requirements for a specific release.</summary>
        VersionSelectionResult EvaluateCandidate(GameReleaseCandidate candidate, string targetPath);

        /// <summary>Get latest game releases across all enabled sources concurrently.</summary>
        Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default);
    }
}
