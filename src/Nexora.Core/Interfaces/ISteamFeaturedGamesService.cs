using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    public interface ISteamFeaturedGamesService
    {
        Task<IReadOnlyList<SteamFeaturedGame>> GetFeaturedGamesAsync(
            int desiredCount = 10,
            CancellationToken cancellationToken = default,
            IProgress<IReadOnlyList<SteamFeaturedGame>>? progress = null);
    }
}
