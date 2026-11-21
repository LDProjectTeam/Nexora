using System;
using Nexora.Core.Helpers;
using Nexora.Core.Services;

namespace Nexora.Core.Models
{
    /// <summary>
    /// Calculated disk space requirements for installing a game version onto a target drive.
    /// </summary>
    public class DiskSpaceRequirement
    {
        public string TargetInstallPath { get; set; } = string.Empty;
        public string DriveRoot { get; set; } = string.Empty;
        public string DriveName { get; set; } = string.Empty;
        public GameVersionType VersionType { get; set; }

        /// <summary>Raw download size.</summary>
        public long DownloadSizeBytes { get; set; }

        /// <summary>Estimated size of game files after extraction/installation.</summary>
        public long EstimatedInstalledSizeBytes { get; set; }

        /// <summary>
        /// Total minimum free space required before starting download.
        /// Accounts for download size, temporary extraction space, and safety buffer.
        /// </summary>
        public long RequiredFreeSpaceBytes { get; set; }

        /// <summary>Actual available free space on the destination drive.</summary>
        public long AvailableFreeSpaceBytes { get; set; }

        /// <summary>Safety buffer (e.g. 1 GB) to ensure OS disk stability.</summary>
        public long SafetyBufferBytes { get; set; } = 1024L * 1024 * 1024;

        /// <summary>True if drive has enough space for the full download and installation.</summary>
        public bool HasEnoughSpace => AvailableFreeSpaceBytes >= RequiredFreeSpaceBytes;

        /// <summary>Missing bytes if space is insufficient (0 if enough).</summary>
        public long MissingSpaceBytes => Math.Max(0, RequiredFreeSpaceBytes - AvailableFreeSpaceBytes);

        // Formatted helpers
        public string FormattedAvailable => PathHelper.FormatBytes(AvailableFreeSpaceBytes);
        public string FormattedRequired => PathHelper.FormatBytes(RequiredFreeSpaceBytes);
        public string FormattedMissing => PathHelper.FormatBytes(MissingSpaceBytes);
        public string FormattedDownloadSize => PathHelper.FormatBytes(DownloadSizeBytes);
        public string FormattedInstalledSize => PathHelper.FormatBytes(EstimatedInstalledSizeBytes);

        public string SummaryText => HasEnoughSpace
            ? string.Format(Localizer.T("DiskSpaceEnoughFormat", "Свободно {0} из требуемых {1} — места достаточно."), FormattedAvailable, FormattedRequired)
            : string.Format(Localizer.T("DiskSpaceNotEnoughFormat", "Недостаточно места на диске {0}! Доступно: {1}, требуется: {2}, не хватает: {3}."), DriveRoot, FormattedAvailable, FormattedRequired, FormattedMissing);

        /// <summary>
        /// Free space needed to finish the download.
        /// <paramref name="alreadyDownloadedBytes"/> must be the number of payload
        /// bytes that already occupy the target drive. Torrent engines preallocate
        /// the entire file set when a torrent is added, so on resume the payload can
        /// take its final size on disk even though only part of the pieces arrived.
        /// Passing the on-disk size therefore avoids demanding the same space twice.
        /// </summary>
        public long CalculateRemainingDownloadBytes(long alreadyDownloadedBytes = 0)
        {
            var remaining = Math.Max(0, DownloadSizeBytes - Math.Max(0, alreadyDownloadedBytes));
            return remaining + SafetyBufferBytes;
        }

        public bool HasEnoughSpaceForDownload(long alreadyDownloadedBytes = 0)
        {
            return AvailableFreeSpaceBytes >= CalculateRemainingDownloadBytes(alreadyDownloadedBytes);
        }

        public string GetDownloadSummaryText(long alreadyDownloadedBytes = 0)
        {
            var required = CalculateRemainingDownloadBytes(alreadyDownloadedBytes);
            var missing = Math.Max(0, required - AvailableFreeSpaceBytes);
            return AvailableFreeSpaceBytes >= required
                ? string.Format(Localizer.T("DiskSpaceEnoughFormat", "Свободно {0} из требуемых {1} — места достаточно."), FormattedAvailable, PathHelper.FormatBytes(required))
                : string.Format(Localizer.T("DiskSpaceNotEnoughFormat", "Недостаточно места на диске {0}! Доступно: {1}, требуется: {2}, не хватает: {3}."), DriveRoot, FormattedAvailable, PathHelper.FormatBytes(required), PathHelper.FormatBytes(missing));
        }
    }
}
