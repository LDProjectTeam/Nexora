using System;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Verifies integrity of installed game files against known checksums.
    /// </summary>
    public interface IFileVerifier
    {
        /// <summary>Verify all files in installPath against the definition's checksums.</summary>
        Task<VerificationResult> VerifyAsync(
            GameDefinition definition,
            string installPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>Compute the SHA-256 hash of a single file.</summary>
        Task<string> ComputeHashAsync(string filePath, CancellationToken ct = default);
    }
}
