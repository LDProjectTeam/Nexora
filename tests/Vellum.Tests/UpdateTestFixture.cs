using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;

namespace Vellum.Tests
{
    [CollectionDefinition("Update", DisableParallelization = true)]
    public sealed class UpdateCollection
    {
    }

    internal sealed class UpdateFixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly TestSigningKey _key = new();

        public UpdateFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "VellumUpdateTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Server = new LocalUpdateServer();

            Settings = new SettingsService(NullLogger<SettingsService>.Instance);
            Settings.Settings.RequireSignedManifests = true;
            Settings.Settings.UpdateTrustedPublicKeys.Clear();
            Settings.Settings.UpdateTrustedPublicKeys.Add(_key.PublicKeyPem);

            Signature = new ManifestSignatureVerifier(Settings, NullLogger<ManifestSignatureVerifier>.Instance);
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Provider = new HttpUpdateManifestProvider(_http, Settings, Signature,
                NullLogger<HttpUpdateManifestProvider>.Instance);
            Diff = new FileDiffService(NullLogger<FileDiffService>.Instance);
            Integrity = new IntegrityVerifier(NullLogger<IntegrityVerifier>.Instance);
            Downloader = new UpdateDownloadManager(_http, Integrity,
                NullLogger<UpdateDownloadManager>.Instance);
            Store = new UpdateStateStore(NullLogger<UpdateStateStore>.Instance);
            Disk = new StubDiskSpace();
            Installer = new UpdateInstaller(Integrity, Store, Disk,
                NullLogger<UpdateInstaller>.Instance);
            Launcher = new StubLauncher();
            Service = new GameUpdateService(Provider, Diff, Downloader, Installer, Store, Settings, Launcher,
                NullLogger<GameUpdateService>.Instance, Disk);
        }

        public string Root { get; }
        public LocalUpdateServer Server { get; }
        public SettingsService Settings { get; }
        public ManifestSignatureVerifier Signature { get; }
        public HttpUpdateManifestProvider Provider { get; }
        public FileDiffService Diff { get; }
        public IntegrityVerifier Integrity { get; }
        public UpdateDownloadManager Downloader { get; }
        public UpdateStateStore Store { get; }
        public StubDiskSpace Disk { get; }
        public UpdateInstaller Installer { get; }
        public StubLauncher Launcher { get; }
        public GameUpdateService Service { get; }

        public string NewGameId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

        public string Sign(byte[] payload) => _key.Sign(payload);

        public string CreateInstallRoot(string gameId, Dictionary<string, byte[]>? files = null)
        {
            var root = Path.Combine(Root, gameId, "install");
            Directory.CreateDirectory(root);
            if (files != null)
                WriteFiles(root, files);
            return root;
        }

        public static void WriteFiles(string root, Dictionary<string, byte[]> files)
        {
            foreach (var (path, data) in files)
            {
                var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, data);
            }
        }

        public async Task<UpdateManifest> PublishManifestAsync(
            string gameId,
            string version,
            string buildId,
            Dictionary<string, byte[]> files,
            IEnumerable<string>? deletedFiles = null,
            bool sign = true,
            TestSigningKey? signingKey = null,
            string urlPrefix = "files")
        {
            var manifest = new UpdateManifest
            {
                GameId = gameId,
                Version = version,
                BuildId = buildId,
                DeletedFiles = deletedFiles?.ToList() ?? new List<string>()
            };

            foreach (var (path, data) in files)
            {
                var url = Server.Url($"{urlPrefix}/{gameId}/{version}/{path}");
                Server.Put($"{urlPrefix}/{gameId}/{version}/{path}", data);
                manifest.Files.Add(new ManifestFile
                {
                    Path = path,
                    Size = data.Length,
                    Sha256 = await HashHelper.ComputeSha256Async(WriteTemp(data)),
                    Url = url
                });
            }

            var manifestPath = $"{urlPrefix}/{gameId}/{version}/manifest.json";
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var payload = System.Text.Encoding.UTF8.GetBytes(json);
            var key = signingKey ?? _key;
            Server.Put(manifestPath, payload,
                sign ? key.Sign(payload) : null,
                sign ? "test-key" : null);
            return manifest;
        }

        public string ManifestUrl(string gameId, string version, string prefix = "files")
            => Server.Url($"{prefix}/{gameId}/{version}/manifest.json");

        public GameDefinition Definition(string gameId, string manifestUrl, string version = "1.0.0")
            => new()
            {
                Id = gameId,
                Name = gameId,
                Version = version,
                InstallSubfolder = gameId,
                ManifestUrl = manifestUrl
            };

        public GameInstallation Installation(string gameId, string root, string version, string buildId)
            => new()
            {
                GameId = gameId,
                DisplayName = gameId,
                InstallPath = root,
                InstalledVersion = version,
                InstalledBuildId = buildId,
                State = InstallState.Installed,
                UpdateAvailable = false
            };

        public static string WriteTemp(byte[] data)
        {
            var path = Path.Combine(Path.GetTempPath(), "vellum-hash-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, data);
            return path;
        }

        public static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

        public void Dispose()
        {
            Service.Dispose();
            Server.Dispose();
            _http.Dispose();
            _key.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
