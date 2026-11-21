using System;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Handles extraction and atomic placement of downloaded game files.
    /// </summary>
    public interface IInstaller
    {
        /// <summary>
        /// Install game from a staging/temp directory to the final install path.
        /// Uses temp dir during extraction, then atomically moves to targetPath.
        /// </summary>
        Task<OperationResult> InstallFromStagingAsync(
            string gameId,
            string stagingPath,
            string targetPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default,
            InstallOptions? options = null);

        /// <summary>Uninstall: remove all game files from installPath.</summary>
        Task<OperationResult> UninstallAsync(string gameId, string installPath);
    }
}
