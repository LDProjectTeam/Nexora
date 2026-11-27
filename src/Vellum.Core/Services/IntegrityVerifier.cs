using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>SHA-256 based integrity verification for staged and installed files.</summary>
    public class IntegrityVerifier : IIntegrityVerifier
    {
        private readonly ILogger<IntegrityVerifier> _logger;

        public IntegrityVerifier(ILogger<IntegrityVerifier> logger)
        {
            _logger = logger;
        }

        public async Task<bool> VerifyFileAsync(string filePath, long expectedSize, string expectedSha256,
            CancellationToken ct = default)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length != expectedSize)
                    return false;

                var actual = await HashHelper.ComputeSha256Async(filePath, ct).ConfigureAwait(false);
                return HashHelper.HashesMatch(actual, expectedSha256);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify {File}", filePath);
                return false;
            }
        }

        public async Task<ManifestVerificationResult> VerifyRootAsync(
            UpdateManifest manifest,
            string root,
            bool requireAllFiles,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var result = new ManifestVerificationResult { Passed = true };
            var total = manifest.Files.Count;
            var completed = 0;

            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                if (!SafeRelativePath.TryResolve(root, file.Path, out var fullPath))
                {
                    result.Problems.Add(new FileChange
                    {
                        Path = file.Path,
                        Kind = FileChangeKind.Corrupted,
                        Size = file.Size,
                        Sha256 = file.Sha256,
                        Url = file.Url
                    });
                    result.Passed = false;
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    if (requireAllFiles)
                    {
                        result.Problems.Add(new FileChange
                        {
                            Path = file.Path,
                            Kind = FileChangeKind.Added,
                            Size = file.Size,
                            Sha256 = file.Sha256,
                            Url = file.Url
                        });
                        result.Passed = false;
                    }
                }
                else if (!await VerifyFileAsync(fullPath, file.Size, file.Sha256, ct).ConfigureAwait(false))
                {
                    result.Problems.Add(new FileChange
                    {
                        Path = file.Path,
                        Kind = FileChangeKind.Corrupted,
                        Size = file.Size,
                        Sha256 = file.Sha256,
                        Url = file.Url
                    });
                    result.Passed = false;
                }
                else
                {
                    result.VerifiedBytes += file.Size;
                }

                completed++;
                progress?.Report(new DownloadProgress
                {
                    GameId = manifest.GameId,
                    Fraction = total > 0 ? (double)completed / total : 0,
                    Phase = Localizer.T("UpdatePhaseVerifying", "Verifying update…"),
                    PhaseKey = DownloadPhases.Verifying,
                    CurrentFile = file.Path
                });
            }

            return result;
        }
    }
}
