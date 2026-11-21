using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Resolves a game to its official Steam Store page and returns localized
    /// store metadata (release date, description, developer, publisher, genres).
    /// Implementations must be safe to call concurrently and must never block
    /// the UI thread.
    /// </summary>
    public interface ISteamMetadataService
    {
        /// <summary>
        /// Returns cached metadata when possible. Network requests run in the
        /// background and are deduplicated per game and language.
        /// </summary>
        Task<SteamMetadataLookupResult> GetMetadataAsync(
            SteamMetadataRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Same as <see cref="GetMetadataAsync(SteamMetadataRequest, CancellationToken)"/>
        /// but can bypass the cache. Used by the manual "refresh metadata" action.
        /// </summary>
        Task<SteamMetadataLookupResult> GetMetadataAsync(
            SteamMetadataRequest request,
            bool forceRefresh,
            CancellationToken cancellationToken = default);
    }
}
