using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    [Collection("Update")]
    public class DownloadAndInstallTests
    {
        [Fact]
        public async Task Download_resumes_partial_file_with_range()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("resume");
            var oldBytes = UpdateFixture.Bytes("old-content-0123456789");
            var newBytes = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
            var root = fx.CreateInstallRoot(gameId, new Dictionary<string, byte[]> { ["a.bin"] = oldBytes });
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200",
                new Dictionary<string, byte[]> { ["a.bin"] = newBytes });
            var previous = await Records(new Dictionary<string, byte[]> { ["a.bin"] = oldBytes });
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan { Manifest = manifest, Diff = diff, InstallRoot = root };

            var staging = PathHelper.GetUpdateStagingPath(root, gameId);
            var partial = Path.Combine(staging, "partial", "a.bin.part");
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            await File.WriteAllBytesAsync(partial, newBytes.Take(1000).ToArray());

            var result = await fx.Downloader.DownloadAsync(plan, staging, 0, 1);
            Assert.True(result.Success, result.ErrorMessage);

            var range = fx.Server.LastRange($"files/{gameId}/2.0.0/a.bin");
            Assert.Equal("bytes=1000-", range);
            var downloaded = await File.ReadAllBytesAsync(Path.Combine(staging, "payload", "a.bin"));
            Assert.Equal(newBytes, downloaded);
        }

        [Fact]
        public async Task Interrupted_download_keeps_partial_file_for_resume()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("interrupt");
            var oldBytes = UpdateFixture.Bytes("old");
            var newBytes = new byte[512 * 1024];
            new Random(7).NextBytes(newBytes);
            var root = fx.CreateInstallRoot(gameId, new Dictionary<string, byte[]> { ["big.bin"] = oldBytes });
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200",
                new Dictionary<string, byte[]> { ["big.bin"] = newBytes });
            fx.Server.Slow($"files/{gameId}/2.0.0/big.bin", chunkSize: 16 * 1024, delayMs: 60);
            var previous = await Records(new Dictionary<string, byte[]> { ["big.bin"] = oldBytes });
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan { Manifest = manifest, Diff = diff, InstallRoot = root };
            var staging = PathHelper.GetUpdateStagingPath(root, gameId);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var result = await fx.Downloader.DownloadAsync(plan, staging, 0, 1, null, cts.Token);

            Assert.True(result.Paused);
            var partial = Path.Combine(staging, "partial", "big.bin.part");
            Assert.True(File.Exists(partial));
            var length = new FileInfo(partial).Length;
            Assert.True(length > 0 && length < newBytes.Length);
        }

        [Fact]
        public async Task Transient_network_error_is_retried()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("retry");
            var oldBytes = UpdateFixture.Bytes("old");
            var newBytes = UpdateFixture.Bytes("new-content");
            var root = fx.CreateInstallRoot(gameId, new Dictionary<string, byte[]> { ["a.bin"] = oldBytes });
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200",
                new Dictionary<string, byte[]> { ["a.bin"] = newBytes });
            fx.Server.FailFirst($"files/{gameId}/2.0.0/a.bin", 2);
            var previous = await Records(new Dictionary<string, byte[]> { ["a.bin"] = oldBytes });
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan { Manifest = manifest, Diff = diff, InstallRoot = root };

            var result = await fx.Downloader.DownloadAsync(
                plan, PathHelper.GetUpdateStagingPath(root, gameId), 0, 1);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(fx.Server.CountRequests($"files/{gameId}/2.0.0/a.bin") >= 3);
        }

        [Fact]
        public async Task Wrong_sha256_is_not_installed()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("badhash");
            var oldBytes = UpdateFixture.Bytes("old");
            var root = fx.CreateInstallRoot(gameId, new Dictionary<string, byte[]> { ["a.bin"] = oldBytes });
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200",
                new Dictionary<string, byte[]> { ["a.bin"] = UpdateFixture.Bytes("correct") });
            // Replace the served bytes after the manifest was signed.
            fx.Server.Put($"files/{gameId}/2.0.0/a.bin", UpdateFixture.Bytes("tampered"));
            var previous = await Records(new Dictionary<string, byte[]> { ["a.bin"] = oldBytes });
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan { Manifest = manifest, Diff = diff, InstallRoot = root };
            var staging = PathHelper.GetUpdateStagingPath(root, gameId);

            var result = await fx.Downloader.DownloadAsync(plan, staging, 0, 1);

            Assert.False(result.Success);
            Assert.False(File.Exists(Path.Combine(staging, "payload", "a.bin")));
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Path.Combine(root, "a.bin")));
        }

        [Fact]
        public async Task Installer_applies_update_and_rollback_restores_previous_build()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("install");
            var oldFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("old-a"),
                ["remove.bin"] = UpdateFixture.Bytes("old-remove")
            };
            var root = fx.CreateInstallRoot(gameId, oldFiles);
            var newFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("new-a"),
                ["added.bin"] = UpdateFixture.Bytes("new-added")
            };
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200", newFiles,
                deletedFiles: new[] { "remove.bin" });
            var previous = await Records(oldFiles);
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan
            {
                Manifest = manifest,
                Diff = diff,
                InstallRoot = root,
                PreviousManifest = previous
            };
            var staging = PathHelper.GetUpdateStagingPath(root, gameId);
            var download = await fx.Downloader.DownloadAsync(plan, staging, 0, 2);
            Assert.True(download.Success, download.ErrorMessage);

            await fx.Store.LoadAsync();
            var state = new UpdatePersistedState();
            state.Games[gameId] = new GameUpdateRecord
            {
                GameId = gameId,
                InstallPath = root,
                CurrentVersion = "1.0.0",
                BuildId = "100",
                InstalledFiles = previous
            };

            var applied = await fx.Installer.ApplyAsync(plan, staging, state);
            Assert.True(applied.Success, applied.ErrorMessage);
            Assert.Equal("new-a", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "a.bin"))));
            Assert.True(File.Exists(Path.Combine(root, "added.bin")));
            Assert.False(File.Exists(Path.Combine(root, "remove.bin")));
            Assert.Equal("2.0.0", state.Games[gameId].CurrentVersion);

            var rollback = await fx.Installer.RollbackAsync(gameId, root);
            Assert.True(rollback.Success, rollback.ErrorMessage);
            Assert.Equal("old-a", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "a.bin"))));
            Assert.True(File.Exists(Path.Combine(root, "remove.bin")));
            Assert.False(File.Exists(Path.Combine(root, "added.bin")));
        }

        [Fact]
        public async Task Failed_install_restores_the_previous_files()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("fail");
            var oldFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("old-a"),
                ["b.bin"] = UpdateFixture.Bytes("old-b")
            };
            var root = fx.CreateInstallRoot(gameId, oldFiles);
            var newFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("new-a"),
                ["b.bin"] = UpdateFixture.Bytes("new-b"),
                ["c.bin"] = UpdateFixture.Bytes("new-c")
            };
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200", newFiles);
            var previous = await Records(oldFiles);
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan
            {
                Manifest = manifest, Diff = diff, InstallRoot = root, PreviousManifest = previous
            };
            var staging = PathHelper.GetUpdateStagingPath(root, gameId);
            Assert.True((await fx.Downloader.DownloadAsync(plan, staging, 0, 2)).Success);

            var state = new UpdatePersistedState();
            state.Games[gameId] = new GameUpdateRecord { GameId = gameId, InstallPath = root };
            fx.Installer.OperationAppliedHook = (count, _) =>
            {
                if (count == 2) throw new IOException("simulated disk failure");
            };

            var result = await fx.Installer.ApplyAsync(plan, staging, state);

            Assert.False(result.Success);
            Assert.True(result.ErrorMessage!.Contains("restored", StringComparison.OrdinalIgnoreCase),
                result.ErrorMessage);
            Assert.Equal("old-a", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "a.bin"))));
            Assert.Equal("old-b", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "b.bin"))));
            Assert.False(File.Exists(Path.Combine(root, "c.bin")));
        }

        [Fact]
        public async Task Recovery_rolls_back_interrupted_install()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("recover");
            var oldFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("old-a"),
                ["b.bin"] = UpdateFixture.Bytes("old-b")
            };
            var root = fx.CreateInstallRoot(gameId, oldFiles);
            var newFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("new-a"),
                ["b.bin"] = UpdateFixture.Bytes("new-b"),
                ["c.bin"] = UpdateFixture.Bytes("new-c")
            };
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200", newFiles);
            var previous = await Records(oldFiles);
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan
            {
                Manifest = manifest, Diff = diff, InstallRoot = root, PreviousManifest = previous
            };
            var staging = PathHelper.GetUpdateStagingPath(root, gameId);
            Assert.True((await fx.Downloader.DownloadAsync(plan, staging, 0, 2)).Success);

            var state = new UpdatePersistedState();
            state.Games[gameId] = new GameUpdateRecord { GameId = gameId, InstallPath = root };
            fx.Installer.SuppressRollbackForTests = true;
            fx.Installer.OperationAppliedHook = (count, _) =>
            {
                if (count == 1) throw new IOException("simulated crash");
            };
            var failed = await fx.Installer.ApplyAsync(plan, staging, state);
            Assert.False(failed.Success);

            var recovered = await fx.Installer.RecoverAsync();
            Assert.Contains(gameId, recovered);
            Assert.Equal("old-a", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "a.bin"))));
            Assert.Equal("old-b", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "b.bin"))));
            Assert.False(File.Exists(Path.Combine(root, "c.bin")));
        }

        [Fact]
        public async Task Insufficient_disk_space_fails_before_modifying_files()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("disk");
            var oldFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("old-a")
            };
            var root = fx.CreateInstallRoot(gameId, oldFiles);
            var newFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("new-a")
            };
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200", newFiles);
            var previous = await Records(oldFiles);
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, true);
            var plan = new GameUpdatePlan
            {
                Manifest = manifest, Diff = diff, InstallRoot = root, PreviousManifest = previous
            };
            var staging = PathHelper.GetUpdateStagingPath(root, gameId);
            Assert.True((await fx.Downloader.DownloadAsync(plan, staging, 0, 1)).Success);
            fx.Disk.FreeBytes = 0;

            var state = new UpdatePersistedState();
            state.Games[gameId] = new GameUpdateRecord { GameId = gameId, InstallPath = root };
            var result = await fx.Installer.ApplyAsync(plan, staging, state);

            Assert.False(result.Success);
            Assert.Contains("space", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("old-a", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "a.bin"))));
        }

        private static async Task<List<InstalledFileRecord>> Records(Dictionary<string, byte[]> files)
        {
            var result = new List<InstalledFileRecord>();
            foreach (var (path, data) in files)
            {
                result.Add(new InstalledFileRecord
                {
                    Path = path,
                    Size = data.Length,
                    Sha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(data))
                });
            }
            return result;
        }
    }
}
