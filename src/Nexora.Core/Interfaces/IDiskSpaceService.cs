using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Calculates and validates disk space requirements prior to downloading or installing games.
    /// </summary>
    public interface IDiskSpaceService
    {
        /// <summary>
        /// Calculates disk space required for a release candidate on the target install path.
        /// Accounts for version type (Portable vs Installer/Repack multiplier).
        /// </summary>
        DiskSpaceRequirement CalculateRequirement(GameReleaseCandidate candidate, string targetInstallPath);

        /// <summary>
        /// Calculates disk space required for a known game definition on the target install path.
        /// </summary>
        DiskSpaceRequirement CalculateRequirement(GameDefinition definition, string targetInstallPath);

        /// <summary>
        /// Gets available free space in bytes for the drive hosting targetPath.
        /// </summary>
        long GetAvailableFreeSpace(string targetPath, out string driveRoot);
    }
}
