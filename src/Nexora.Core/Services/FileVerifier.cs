using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    public class FileVerifier : IFileVerifier
    {
        private readonly ILogger<FileVerifier> _logger;

        public FileVerifier(ILogger<FileVerifier> logger)
        {
            _logger = logger;
        }

        public async Task<VerificationResult> VerifyAsync(
            GameDefinition definition,
            string installPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var result = new VerificationResult { Passed = true };
            var checksums = definition.Checksums;

            if (checksums.Files.Count == 0)
            {
                _logger.LogWarning("No checksums defined for {GameId}, skipping verification.", definition.Id);
                result.Passed = true;
                return result;
            }

            int total = checksums.Files.Count;
            int done = 0;

            foreach (var (relativePath, expectedHash) in checksums.Files)
            {
                ct.ThrowIfCancellationRequested();

                var fullPath = Path.Combine(installPath, relativePath);

                if (!File.Exists(fullPath))
                {
                    _logger.LogWarning("Missing file: {Path}", fullPath);
                    result.MissingFiles.Add(relativePath);
                    result.Passed = false;
                    done++;
                    continue;
                }

                try
                {
                    var actualHash = await HashHelper.ComputeSha256Async(fullPath, ct).ConfigureAwait(false);
                    if (!HashHelper.HashesMatch(actualHash, expectedHash))
                    {
                        _logger.LogWarning("Hash mismatch for {File}: expected {Expected}, got {Actual}",
                            relativePath, expectedHash, actualHash);
                        result.FailedFiles.Add(relativePath);
                        result.Passed = false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error verifying {File}", fullPath);
                    result.FailedFiles.Add(relativePath);
                    result.Passed = false;
                }

                done++;
                progress?.Report(new DownloadProgress
                {
                    GameId = definition.Id,
                    Fraction = (double)done / total,
                    Phase = "Verifying",
                    CurrentFile = relativePath
                });
            }

            _logger.LogInformation("Verification for {GameId}: {Result}", definition.Id,
                result.Passed ? "PASSED" : $"FAILED ({result.FailedFiles.Count + result.MissingFiles.Count} issues)");
            return result;
        }

        public Task<string> ComputeHashAsync(string filePath, CancellationToken ct = default)
            => HashHelper.ComputeSha256Async(filePath, ct);
    }
}
