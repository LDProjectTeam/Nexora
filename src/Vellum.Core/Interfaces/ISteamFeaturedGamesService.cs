using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    public interface ISteamFeaturedGamesService
    {
        Task<IReadOnlyList<SteamFeaturedGame>> GetFeaturedGamesAsync(
            int desiredCount = 10,
            CancellationToken cancellationToken = default,
            IProgress<IReadOnlyList<SteamFeaturedGame>>? progress = null);
    }
}
