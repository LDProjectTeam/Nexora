using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    public enum ArtworkPriority
    {
        Background = 0,
        Visible = 1,
        ModalDetails = 2
    }

    public interface IGameArtworkService
    {
        Task<GameArtwork?> FindArtworkAsync(string gameTitle, CancellationToken cancellationToken = default);
        Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, CancellationToken cancellationToken = default);
        Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, ArtworkPriority priority, CancellationToken cancellationToken = default);
        Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, ArtworkPriority priority, bool forceRefresh, CancellationToken cancellationToken = default);
    }
}
