using System.Collections.Generic;

namespace Vellum.Core.Models
{
    /// <summary>
    /// Result of evaluating all available release versions across sources for a requested game.
    /// Prioritizes: Portable -> Installer / Archive / Repack -> None.
    /// </summary>
    public class VersionSelectionResult
    {
        public bool IsAvailable => SelectedCandidate != null;

        /// <summary>The best release version chosen according to priority rules.</summary>
        public GameReleaseCandidate? SelectedCandidate { get; set; }

        /// <summary>All candidate versions evaluated.</summary>
        public List<GameReleaseCandidate> AllCandidates { get; set; } = new();

        /// <summary>Calculated disk space requirement for the selected candidate.</summary>
        public DiskSpaceRequirement? SpaceRequirement { get; set; }

        /// <summary>Human-readable explanation of why this version was chosen.</summary>
        public string StrategyExplanation { get; set; } = string.Empty;

        /// <summary>Any errors encountered during lookup across sources.</summary>
        public List<string> Errors { get; set; } = new();
    }
}
