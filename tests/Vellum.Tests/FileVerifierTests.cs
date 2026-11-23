using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Helpers;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    public class FileVerifierTests
    {
        private sealed class SyncProgress : IProgress<DownloadProgress>
        {
            public ConcurrentBag<DownloadProgress> Items { get; } = new();

            public void Report(DownloadProgress value) => Items.Add(value);
        }

        [Fact]
        public async Task VerifyAsync_FindsCorruptMissingAndTraversalEntries()
        {
            var root = Path.Combine(Path.GetTempPath(), "Vellum-VerifyTest-" + Guid.NewGuid().ToString("N"));
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);

            var goodPath = Path.Combine(game, "good.bin");
            var badPath = Path.Combine(game, "bad.bin");
            await File.WriteAllTextAsync(goodPath, "good");
            await File.WriteAllTextAsync(badPath, "bad");

            var goodHash = await HashHelper.ComputeSha256Async(goodPath);

            var definition = new GameDefinition
            {
                Id = "verify-test",
                Checksums = new FileChecksums
                {
                    Files = new Dictionary<string, string>
                    {
                        ["good.bin"] = goodHash,
                        ["bad.bin"] = new string('0', 64),
                        ["missing.bin"] = new string('1', 64),
                        ["../escape.bin"] = new string('2', 64)
                    }
                }
            };

            try
            {
                var verifier = new FileVerifier(NullLogger<FileVerifier>.Instance);
                var progress = new SyncProgress();

                var result = await verifier.VerifyAsync(definition, game, progress);

                Assert.False(result.Passed);
                Assert.Contains("bad.bin", result.FailedFiles);
                Assert.Contains("../escape.bin", result.FailedFiles);
                Assert.Contains("missing.bin", result.MissingFiles);
                Assert.DoesNotContain("good.bin", result.FailedFiles);
                Assert.DoesNotContain("good.bin", result.MissingFiles);
                Assert.Equal(2, result.FailedFiles.Count);
                Assert.Single(result.MissingFiles);

                // Every entry reports progress exactly once and the parallel pass ends at 100%.
                Assert.Equal(4, progress.Items.Count);
                Assert.Equal(1.0, progress.Items.Max(item => item.Fraction), 3);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
