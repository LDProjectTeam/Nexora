using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vellum.Core.Models;
using Xunit;

namespace Vellum.Tests
{
    [Collection("Update")]
    public class GameUpdateServiceTests
    {
        [Fact]
        public async Task Check_detects_update_and_start_applies_it()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("service");
            var oldFiles = new Dictionary<string, byte[]> { ["a.bin"] = UpdateFixture.Bytes("old-a") };
            var root = fx.CreateInstallRoot(gameId, oldFiles);
            var newFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("new-a"),
                ["new.bin"] = UpdateFixture.Bytes("new-file")
            };
            await fx.PublishManifestAsync(gameId, "2.0.0", "200", newFiles);
            var definition = fx.Definition(gameId, fx.ManifestUrl(gameId, "2.0.0"));
            var installation = fx.Installation(gameId, root, "1.0.0", "100");

            var check = await fx.Service.CheckForUpdateAsync(definition, installation, force: true);
            Assert.True(check.UpdateAvailable);
            Assert.Equal(GameUpdatePhase.UpdateAvailable, check.Status.Phase);
            Assert.Equal(1, check.Status.AddedFileCount);
            Assert.Equal(1, check.Status.ChangedFileCount);

            var result = await fx.Service.StartUpdateAsync(gameId);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("new-a", System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(root, "a.bin"))));
            Assert.True(File.Exists(Path.Combine(root, "new.bin")));
            Assert.Equal(GameUpdatePhase.UpToDate, fx.Service.GetStatus(gameId).Phase);
            Assert.Equal("2.0.0", fx.Service.GetStatus(gameId).CurrentVersion);
        }

        [Fact]
        public async Task Queue_serializes_updates_for_multiple_games()
        {
            using var fx = new UpdateFixture();
            fx.Settings.Settings.MaxConcurrentDownloads = 1;

            var games = new List<(string Id, GameDefinition Definition, GameInstallation Installation)>();
            foreach (var suffix in new[] { "q1", "q2" })
            {
                var gameId = fx.NewGameId(suffix);
                var oldFiles = new Dictionary<string, byte[]> { ["a.bin"] = UpdateFixture.Bytes("old") };
                var root = fx.CreateInstallRoot(gameId, oldFiles);
                var newBytes = new byte[256 * 1024];
                new Random(suffix.GetHashCode()).NextBytes(newBytes);
                var newFiles = new Dictionary<string, byte[]> { ["a.bin"] = newBytes };
                await fx.PublishManifestAsync(gameId, "2.0.0", "200", newFiles);
                fx.Server.Slow($"files/{gameId}/2.0.0/a.bin", chunkSize: 8 * 1024, delayMs: 50);
                var definition = fx.Definition(gameId, fx.ManifestUrl(gameId, "2.0.0"));
                var installation = fx.Installation(gameId, root, "1.0.0", "100");
                await fx.Service.CheckForUpdateAsync(definition, installation, true);
                games.Add((gameId, definition, installation));
            }

            var first = fx.Service.StartUpdateAsync(games[0].Id);
            await Task.Delay(150);
            var second = fx.Service.StartUpdateAsync(games[1].Id);
            await Task.Delay(500);

            var phases = games.Select(g => fx.Service.GetStatus(g.Id).Phase).ToList();
            Assert.Contains(GameUpdatePhase.Downloading, phases);
            Assert.Contains(GameUpdatePhase.WaitingForDownload, phases);

            await fx.Service.PauseAllAsync();
            await Task.WhenAny(Task.WhenAll(first, second), Task.Delay(5000));
        }

        [Fact]
        public async Task Update_state_is_persisted_for_the_next_launcher_start()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("persist");
            var oldFiles = new Dictionary<string, byte[]> { ["a.bin"] = UpdateFixture.Bytes("old") };
            var root = fx.CreateInstallRoot(gameId, oldFiles);
            await fx.PublishManifestAsync(gameId, "2.0.0", "200",
                new Dictionary<string, byte[]> { ["a.bin"] = UpdateFixture.Bytes("new") });
            var definition = fx.Definition(gameId, fx.ManifestUrl(gameId, "2.0.0"));
            var installation = fx.Installation(gameId, root, "1.0.0", "100");

            var check = await fx.Service.CheckForUpdateAsync(definition, installation, true);
            Assert.True(check.UpdateAvailable);

            var freshStore = new Vellum.Core.Services.UpdateStateStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Vellum.Core.Services.UpdateStateStore>.Instance);
            await freshStore.LoadAsync();
            Assert.True(freshStore.State.Games.TryGetValue(gameId, out var record));
            Assert.Equal("2.0.0", record.PendingVersion);
            Assert.Equal(GameUpdatePhase.UpdateAvailable, record.Phase);
            Assert.Equal(root, record.InstallPath);
        }

        [Fact]
        public async Task Same_build_with_missing_file_is_offered_as_repair()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("repair");
            var files = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("a"),
                ["missing.bin"] = UpdateFixture.Bytes("missing")
            };
            var root = fx.CreateInstallRoot(gameId, new Dictionary<string, byte[]>
            {
                ["a.bin"] = files["a.bin"]
            });
            var manifest = await fx.PublishManifestAsync(gameId, "1.0.0", "100", files);
            var definition = fx.Definition(gameId, fx.ManifestUrl(gameId, "1.0.0"));
            var installation = fx.Installation(gameId, root, "1.0.0", "100");

            // Seed the record as if the game was installed from this build.
            await fx.Store.LoadAsync();
            fx.Store.State.Games[gameId] = new GameUpdateRecord
            {
                GameId = gameId,
                InstallPath = root,
                CurrentVersion = "1.0.0",
                BuildId = "100",
                InstalledFiles = manifest.Files.Select(f => new InstalledFileRecord
                {
                    Path = f.Path,
                    Size = f.Size,
                    Sha256 = f.Sha256
                }).ToList()
            };

            var check = await fx.Service.CheckForUpdateAsync(definition, installation, force: true);

            Assert.True(check.UpdateAvailable);
            Assert.Equal(1, check.Status.AddedFileCount);
        }

        [Fact]
        public async Task No_manifest_source_reports_no_update_without_error()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("nosource");
            var root = fx.CreateInstallRoot(gameId, new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("old")
            });
            var definition = new GameDefinition { Id = gameId, Name = gameId };
            var installation = fx.Installation(gameId, root, "1.0.0", "100");

            var check = await fx.Service.CheckForUpdateAsync(definition, installation, true);

            Assert.False(check.SourceConfigured);
            Assert.False(check.UpdateAvailable);
            Assert.Equal(GameUpdatePhase.Unknown, check.Status.Phase);
            Assert.True(string.IsNullOrEmpty(check.Status.ErrorMessage));
        }
    }
}
