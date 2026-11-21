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

            var entries = checksums.Files.ToList();
            var total = entries.Count;
            var done = 0;
            var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
            var missing = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Hashing is I/O + CPU work; a few files in parallel makes verifying a large
            // game several times faster without saturating the disk queue.
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
                CancellationToken = ct
            };

            var fullRoot = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

            void ReportDone(string relativePath)
            {
                var completed = Interlocked.Increment(ref done);
                progress?.Report(new DownloadProgress
                {
                    GameId = definition.Id,
                    Fraction = (double)completed / total,
                    Phase = "Verifying",
                    PhaseKey = DownloadPhases.Verifying,
                    CurrentFile = relativePath
                });
            }

            await Parallel.ForEachAsync(entries, options, async (entry, token) =>
            {
                var relativePath = entry.Key;
                var expectedHash = entry.Value;

                string resolvedPath;
                try
                {
                    resolvedPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    _logger.LogWarning("Invalid checksum path: {Path}", relativePath);
                    failed.Add(relativePath);
                    ReportDone(relativePath);
                    return;
                }

                // A remote catalog must not be able to make the verifier hash files
                // outside the game folder (../ traversal).
                if (!resolvedPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Checksum path escapes the install folder: {Path}", relativePath);
                    failed.Add(relativePath);
                    ReportDone(relativePath);
                    return;
                }

                if (!File.Exists(resolvedPath))
                {
                    _logger.LogWarning("Missing file: {Path}", resolvedPath);
                    missing.Add(relativePath);
                    ReportDone(relativePath);
                    return;
                }

                try
                {
                    var actualHash = await HashHelper.ComputeSha256Async(resolvedPath, token).ConfigureAwait(false);
                    if (!HashHelper.HashesMatch(actualHash, expectedHash))
                    {
                        _logger.LogWarning("Hash mismatch for {File}: expected {Expected}, got {Actual}",
                            relativePath, expectedHash, actualHash);
                        failed.Add(relativePath);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error verifying {File}", resolvedPath);
                    failed.Add(relativePath);
                }

                ReportDone(relativePath);
            }).ConfigureAwait(false);

            result.FailedFiles.AddRange(failed);
            result.MissingFiles.AddRange(missing);
            result.Passed = failed.IsEmpty && missing.IsEmpty;

            _logger.LogInformation("Verification for {GameId}: {Result}", definition.Id,
                result.Passed ? "PASSED" : $"FAILED ({result.FailedFiles.Count + result.MissingFiles.Count} issues)");
            return result;
        }

        public Task<string> ComputeHashAsync(string filePath, CancellationToken ct = default)
            => HashHelper.ComputeSha256Async(filePath, ct);
    }
}
