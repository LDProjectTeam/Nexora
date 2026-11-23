using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    public enum GameBrowseOrder
    {
        Alphabetical,
        MostDownloaded
    }

    public interface IPagedGameSource : IGameSource
    {
        int PageSize { get; }

        Task<IReadOnlyList<GameReleaseCandidate>> GetGamesPageAsync(
            int page,
            GameBrowseOrder order,
            CancellationToken ct = default);
    }
}
