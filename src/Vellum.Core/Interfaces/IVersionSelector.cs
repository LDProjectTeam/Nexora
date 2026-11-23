using System.Collections.Generic;
using Vellum.Core.Models;

namespace Vellum.Core.Interfaces
{
    /// <summary>
    /// Selects the optimal version of a game among candidates according to strict business rules:
    /// Priority 1: Portable version (always preferred if available in any source).
    /// Priority 2: Installer / Repack / Archive.
    /// Ties are broken by configured Source Priority, seeder count, and release freshness.
    /// </summary>
    public interface IVersionSelector
    {
        VersionSelectionResult SelectBestVersion(
            IEnumerable<GameReleaseCandidate> candidates,
            string targetInstallPath);
    }
}
