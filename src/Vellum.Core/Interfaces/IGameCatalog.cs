using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    /// <summary>
    /// Provides access to the game catalog (list of available games).
    /// Implementations: LocalGameCatalogService (reads games.json),
    /// future: RemoteGameCatalogService.
    /// </summary>
    public interface IGameCatalog
    {
        /// <summary>Load and return all games from all configured sources.</summary>
        Task<IReadOnlyList<GameDefinition>> GetAllGamesAsync(CancellationToken ct = default);

        /// <summary>Get a single game definition by ID.</summary>
        Task<GameDefinition?> GetGameAsync(string gameId, CancellationToken ct = default);

        /// <summary>Reload catalog from disk / network.</summary>
        Task RefreshAsync(CancellationToken ct = default);
    }
}
