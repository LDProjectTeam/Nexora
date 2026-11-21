using System.Collections.Generic;

namespace Nexora.Core.Models
{
    /// <summary>
    /// Result of a file integrity verification pass.
    /// </summary>
    public class VerificationResult
    {
        public bool Passed { get; set; }

        /// <summary>Files that failed verification (path → expected hash).</summary>
        public List<string> FailedFiles { get; set; } = new();

        /// <summary>Files that were missing entirely.</summary>
        public List<string> MissingFiles { get; set; } = new();

        public string Summary => Passed
            ? "All files verified successfully."
            : $"{FailedFiles.Count} file(s) corrupted, {MissingFiles.Count} file(s) missing.";
    }
}
