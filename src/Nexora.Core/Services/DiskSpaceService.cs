using System;
using System.IO;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    public class DiskSpaceService : IDiskSpaceService
    {
        private readonly ILogger<DiskSpaceService> _logger;
        private readonly ISettingsService _settings;

        public DiskSpaceService(ILogger<DiskSpaceService> logger, ISettingsService settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public DiskSpaceRequirement CalculateRequirement(GameReleaseCandidate candidate, string targetInstallPath)
        {
            var available = GetAvailableFreeSpace(targetInstallPath, out var driveRoot);
            var driveName = driveRoot;

            long safetyBuffer = 1024L * 1024 * 1024; // 1 GB safety buffer

            long downloadSize = candidate.DownloadSizeBytes > 0 ? candidate.DownloadSizeBytes : 1024L * 1024 * 500;
            long estimatedInstalledSize = candidate.EstimatedInstalledSizeBytes > 0
                ? candidate.EstimatedInstalledSizeBytes
                : (long)(downloadSize * 1.5);
            long installedSize = candidate.VersionType == GameVersionType.Portable
                ? downloadSize
                : estimatedInstalledSize;

            long required;
            switch (candidate.VersionType)
            {
                case GameVersionType.Portable:
                    // Portable: архив → сразу папка с игрой, архив удаляется
                    required = downloadSize + safetyBuffer;
                    break;

                default:
                    // Repack/Installer/Archive: the downloaded archive is unpacked into the
                    // game folder and removed by the setup. The lasting footprint is the
                    // installed game, not a second copy of the download. Download-time checks
                    // use HasEnoughSpaceForDownload(), and the install-time check credits the
                    // staging folder that is deleted during setup.
                    required = installedSize + safetyBuffer;
                    break;
            }

            return new DiskSpaceRequirement
            {
                TargetInstallPath = targetInstallPath,
                DriveRoot = driveRoot,
                DriveName = driveName,
                VersionType = candidate.VersionType,
                DownloadSizeBytes = downloadSize,
                EstimatedInstalledSizeBytes = installedSize,
                RequiredFreeSpaceBytes = required,
                AvailableFreeSpaceBytes = available,
                SafetyBufferBytes = safetyBuffer
            };
        }

        public DiskSpaceRequirement CalculateRequirement(GameDefinition definition, string targetInstallPath)
        {
            var candidate = new GameReleaseCandidate
            {
                CleanTitle = definition.Name,
                DownloadSizeBytes = definition.DownloadSizeBytes > 0 ? definition.DownloadSizeBytes : definition.SizeBytes,
                EstimatedInstalledSizeBytes = definition.SizeBytes > 0 ? definition.SizeBytes : definition.DownloadSizeBytes,
                VersionType = GameVersionType.Portable // Default built-in
            };

            return CalculateRequirement(candidate, targetInstallPath);
        }

        public long GetAvailableFreeSpace(string targetPath, out string driveRoot)
        {
            driveRoot = @"C:\";
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath))
                    targetPath = @"C:\";

                var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
                if (!string.IsNullOrEmpty(root))
                {
                    driveRoot = root;
                    var driveInfo = new DriveInfo(root);
                    if (driveInfo.IsReady)
                    {
                        return driveInfo.AvailableFreeSpace;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get drive info for {Path}, checking fallback root.", targetPath);
            }

            try
            {
                var systemDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
                driveRoot = systemDrive.Name;
                return systemDrive.AvailableFreeSpace;
            }
            catch
            {
                return 0;
            }
        }

    }
}
