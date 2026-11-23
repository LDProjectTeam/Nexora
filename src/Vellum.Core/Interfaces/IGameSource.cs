using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    /// <summary>
    /// Represents an external game catalog or distribution source.
    /// </summary>
    public interface IGameSource
    {
        /// <summary>Unique identifier of this source.</summary>
        string SourceId { get; }

        /// <summary>User-facing display name.</summary>
        string DisplayName { get; }

        /// <summary>Current priority (lower number = higher priority).</summary>
        int Priority { get; set; }

        /// <summary>Whether this source is currently enabled.</summary>
        bool IsEnabled { get; set; }

        /// <summary>Search this source for games matching the given query.</summary>
        Task<IReadOnlyList<GameReleaseCandidate>> SearchAsync(string query, CancellationToken ct = default);

        /// <summary>Get latest or popular game releases from this source.</summary>
        Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default);
    }
}
