using System;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Checks for and applies game updates.
    /// </summary>
    public interface IUpdater
    {
        /// <summary>Check whether a newer version is available for the given game.</summary>
        Task<GameUpdate?> CheckForUpdateAsync(GameDefinition definition, GameInstallation installation,
            CancellationToken ct = default);

        /// <summary>Download and apply an update.</summary>
        Task<OperationResult> ApplyUpdateAsync(
            GameDefinition definition,
            GameInstallation installation,
            GameUpdate update,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);
    }
}
