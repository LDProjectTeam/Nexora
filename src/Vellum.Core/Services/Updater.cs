using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Microsoft.Extensions.Logging;

namespace Vellum.Core.Services
{
    public class Updater : IUpdater
    {
        private readonly ILogger<Updater> _logger;
        private readonly IDownloadManager _downloadManager;
        private readonly IInstaller _installer;
        private readonly IFileVerifier _verifier;

        public Updater(ILogger<Updater> logger, IDownloadManager downloadManager,
            IInstaller installer, IFileVerifier verifier)
        {
            _logger = logger;
            _downloadManager = downloadManager;
            _installer = installer;
            _verifier = verifier;
        }

        public Task<GameUpdate?> CheckForUpdateAsync(
            GameDefinition definition,
            GameInstallation installation,
            CancellationToken ct = default)
        {
            if (definition.Updates == null || definition.Updates.Count == 0)
                return Task.FromResult<GameUpdate?>(null);

            // Find highest version that is newer than installed
            GameUpdate? latest = null;
            foreach (var update in definition.Updates)
            {
                if (IsNewer(update.Version, installation.InstalledVersion))
                {
                    if (latest == null || IsNewer(update.Version, latest.Version))
                        latest = update;
                }
            }

            return Task.FromResult(latest);
        }

        public async Task<OperationResult> ApplyUpdateAsync(
            GameDefinition definition,
            GameInstallation installation,
            GameUpdate update,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            _logger.LogInformation("Updating {GameId} from {OldVer} to {NewVer}",
                definition.Id, installation.InstalledVersion, update.Version);

            // Use update sources if provided, else use main sources
            var tempDef = new GameDefinition
            {
                Id = definition.Id,
                Name = definition.Name,
                DownloadSources = update.Sources?.Count > 0
                    ? update.Sources
                    : definition.DownloadSources,
                Checksums = definition.Checksums
            };

            var libraryPath = System.IO.Path.GetDirectoryName(installation.InstallPath)
                ?? throw new InvalidOperationException("The installed game has no library directory.");
            var stagingPath = Helpers.PathHelper.GetStagingPath(libraryPath, $"{definition.Id}_update");
            System.IO.Directory.CreateDirectory(stagingPath);

            var downloadResult = await _downloadManager.DownloadAsync(
                tempDef, stagingPath, progress, ct);

            if (!downloadResult.Success)
                return downloadResult;

            var installResult = await _installer.InstallFromStagingAsync(
                definition.Id, stagingPath, installation.InstallPath, progress, ct);

            if (!installResult.Success)
                return installResult;

            Helpers.PathHelper.DeleteEmptyStagingRoot(stagingPath);

            installation.InstalledVersion = update.Version;
            installation.LastUpdatedAt = DateTime.UtcNow;
            installation.UpdateAvailable = false;
            installation.AvailableVersion = null;

            return OperationResult.Ok();
        }

        private static bool IsNewer(string candidate, string installed)
        {
            if (Version.TryParse(candidate, out var cv) && Version.TryParse(installed, out var iv))
                return cv > iv;
            return string.Compare(candidate, installed, StringComparison.OrdinalIgnoreCase) > 0;
        }
    }
}
