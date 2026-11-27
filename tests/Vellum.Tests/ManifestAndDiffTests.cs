using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    [Collection("Update")]
    public class ManifestAndDiffTests
    {
        [Fact]
        public async Task No_update_when_manifest_matches_installed_build()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("same");
            var files = new Dictionary<string, byte[]>
            {
                ["bin/game.exe"] = UpdateFixture.Bytes("game"),
                ["data/pack.bin"] = UpdateFixture.Bytes("pack")
            };
            var root = fx.CreateInstallRoot(gameId, files);
            await fx.PublishManifestAsync(gameId, "1.0.0", "100", files);

            var previous = new List<InstalledFileRecord>();
            foreach (var (path, data) in files)
            {
                previous.Add(new InstalledFileRecord
                {
                    Path = path,
                    Size = data.Length,
                    Sha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(data))
                });
            }

            var result = await fx.Diff.ComputeAsync(
                await fx.Provider.GetGameManifestAsync(gameId, fx.ManifestUrl(gameId, "1.0.0")),
                root, previous, verifyUnchangedFiles: false);

            Assert.True(result.IsUpToDate);
            Assert.Equal(0, result.BytesToDownload);
            Assert.Equal(2, result.UnchangedFileCount);
        }

        [Fact]
        public async Task Detects_changed_new_and_deleted_files()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("diff");
            var oldFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("old-a"),
                ["b.bin"] = UpdateFixture.Bytes("same-b"),
                ["gone.bin"] = UpdateFixture.Bytes("remove-me")
            };
            var root = fx.CreateInstallRoot(gameId, oldFiles);

            var newFiles = new Dictionary<string, byte[]>
            {
                ["a.bin"] = UpdateFixture.Bytes("new-a"),
                ["b.bin"] = UpdateFixture.Bytes("same-b"),
                ["new.bin"] = UpdateFixture.Bytes("fresh")
            };
            var manifest = await fx.PublishManifestAsync(gameId, "2.0.0", "200", newFiles);

            var previous = await HashRecords(oldFiles);
            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, verifyUnchangedFiles: true);

            Assert.Equal(1, diff.ModifiedFileCount);
            Assert.Equal(1, diff.AddedFileCount);
            Assert.Equal(1, diff.DeletedFileCount);
            Assert.Equal(0, diff.CorruptedFileCount);
            Assert.Equal(newFiles["a.bin"].Length + newFiles["new.bin"].Length, diff.BytesToDownload);
            Assert.Contains(diff.Changes, c => c.Path == "gone.bin" && c.Kind == FileChangeKind.Deleted);
        }

        [Fact]
        public async Task Detects_corrupted_file_with_matching_size()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("corrupt");
            var manifestFiles = new Dictionary<string, byte[]>
            {
                ["asset.bin"] = UpdateFixture.Bytes("AAAAAAAA")
            };
            var root = fx.CreateInstallRoot(gameId);
            UpdateFixture.WriteFiles(root, new Dictionary<string, byte[]>
            {
                ["asset.bin"] = UpdateFixture.Bytes("BBBBBBBB")
            });
            var manifest = await fx.PublishManifestAsync(gameId, "1.1.0", "110", manifestFiles);
            var previous = await HashRecords(manifestFiles);

            var diff = await fx.Diff.ComputeAsync(manifest, root, previous, verifyUnchangedFiles: true);

            Assert.Equal(1, diff.CorruptedFileCount);
            Assert.True(diff.BytesToDownload > 0);
        }

        [Fact]
        public async Task Invalid_signature_is_rejected()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("badsig");
            var files = new Dictionary<string, byte[]>
            {
                ["game.exe"] = UpdateFixture.Bytes("v2")
            };
            using var attacker = new TestSigningKey();
            await fx.PublishManifestAsync(gameId, "2.0.0", "200", files, signingKey: attacker);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fx.Provider.GetGameManifestAsync(gameId, fx.ManifestUrl(gameId, "2.0.0")));
            Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Unsigned_manifest_is_rejected_when_signatures_are_required()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("unsigned");
            var files = new Dictionary<string, byte[]>
            {
                ["game.exe"] = UpdateFixture.Bytes("v2")
            };
            await fx.PublishManifestAsync(gameId, "2.0.0", "200", files, sign: false);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fx.Provider.GetGameManifestAsync(gameId, fx.ManifestUrl(gameId, "2.0.0")));
            Assert.Contains("not signed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Path_traversal_in_manifest_is_rejected()
        {
            using var fx = new UpdateFixture();
            var gameId = fx.NewGameId("traversal");
            var manifest = new UpdateManifest
            {
                GameId = gameId,
                Version = "2.0.0",
                BuildId = "200",
                Files =
                {
                    new ManifestFile
                    {
                        Path = "../escape.dll",
                        Size = 1,
                        Sha256 = new string('a', 64),
                        Url = fx.Server.Url("evil")
                    }
                }
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var payload = Encoding.UTF8.GetBytes(json);
            var path = $"files/{gameId}/2.0.0/manifest.json";
            fx.Server.Put(path, payload, fx.Sign(payload), "test-key");

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fx.Provider.GetGameManifestAsync(gameId, fx.Server.Url(path)));
            Assert.Contains("Unsafe file path", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Version_normalization_prevents_a_false_self_update_loop()
        {
            Assert.False(UpdateVersion.IsNewer("1.0.71", "1.0.71.0"));
            Assert.False(UpdateVersion.IsNewer("1.0.71.0", "1.0.71"));
            Assert.True(UpdateVersion.IsNewer("1.0.72", "1.0.71.0"));
            Assert.True(UpdateVersion.AreSameVersion("1.0.72", "1.0.72.0"));
            Assert.False(UpdateVersion.AreSameVersion("1.0.72", "1.0.71.0"));
        }

        [Fact]
        public void Missing_trusted_key_fails_closed()
        {
            using var fx = new UpdateFixture();
            fx.Settings.Settings.UpdateTrustedPublicKeys.Clear();
            var verifier = new ManifestSignatureVerifier(fx.Settings,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ManifestSignatureVerifier>.Instance);
            Assert.False(verifier.HasTrustedKeys);
        }

        private static async Task<List<InstalledFileRecord>> HashRecords(Dictionary<string, byte[]> files)
        {
            var records = new List<InstalledFileRecord>();
            foreach (var (path, data) in files)
            {
                records.Add(new InstalledFileRecord
                {
                    Path = path,
                    Size = data.Length,
                    Sha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(data))
                });
            }
            return records;
        }
    }
}

