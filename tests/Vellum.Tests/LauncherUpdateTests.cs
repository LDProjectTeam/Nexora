using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Helpers;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    [Collection("Update")]
    public class LauncherUpdateTests
    {
        [Fact]
        public async Task Launcher_update_is_detected_downloaded_and_staged()
        {
            using var fx = new UpdateFixture();
            fx.Settings.Settings.AutoDownloadLauncherUpdates = false;
            var version = "9.9.9";
            var entry = UpdateFixture.Bytes("new-launcher");
            var zip = BuildZip(new Dictionary<string, byte[]>
            {
                ["Vellum.exe"] = entry,
                ["Vellum.Core.dll"] = UpdateFixture.Bytes("core")
            });
            var packagePath = $"launcher/{version}/Vellum.zip";
            fx.Server.Put(packagePath, zip);

            var manifest = new LauncherUpdateManifest
            {
                Version = version,
                PackageUrl = fx.Server.Url(packagePath),
                PackageSize = zip.Length,
                PackageSha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(zip)),
                EntryPoint = "Vellum.exe",
                Files =
                {
                    new ManifestFile
                    {
                        Path = "Vellum.exe", Size = entry.Length,
                        Sha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(entry))
                    },
                    new ManifestFile
                    {
                        Path = "Vellum.Core.dll", Size = 4,
                        Sha256 = await HashHelper.ComputeSha256Async(
                            UpdateFixture.WriteTemp(UpdateFixture.Bytes("core")))
                    }
                }
            };

            var manifestPath = $"launcher/{version}/manifest.json";
            var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            fx.Server.Put(manifestPath, payload, fx.Sign(payload), "test-key");
            fx.Settings.Settings.LauncherUpdateManifestUrl = fx.Server.Url(manifestPath);

            var service = new LauncherUpdateService(new System.Net.Http.HttpClient(), fx.Provider,
                fx.Settings, fx.Store, NullLogger<LauncherUpdateService>.Instance);

            var status = await service.CheckForUpdatesAsync("1.0.0");
            Assert.Equal(LauncherUpdatePhase.UpdateAvailable, status.Phase);
            Assert.Equal(version, status.AvailableVersion);

            var download = await service.DownloadAsync("1.0.0");
            Assert.True(download.Success, download.ErrorMessage);
            Assert.Equal(LauncherUpdatePhase.ReadyToInstall, service.Status.Phase);
            Assert.True(File.Exists(Path.Combine(service.Status.StagingDir!, "Vellum.exe")));

            var staged = await File.ReadAllBytesAsync(Path.Combine(service.Status.StagingDir!, "Vellum.exe"));
            Assert.Equal(entry, staged);
        }

        [Fact]
        public async Task Launcher_package_installer_applies_and_rolls_back()
        {
            using var fx = new UpdateFixture();
            var installDir = Path.Combine(fx.Root, "launcher-install");
            var stagingDir = Path.Combine(fx.Root, "launcher-staging");
            var backupDir = Path.Combine(fx.Root, "launcher-backup");
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(stagingDir);

            var oldExe = UpdateFixture.Bytes("old-exe");
            var oldData = UpdateFixture.Bytes("old-data");
            await File.WriteAllBytesAsync(Path.Combine(installDir, "Vellum.exe"), oldExe);
            await File.WriteAllBytesAsync(Path.Combine(installDir, "old.dll"), oldData);

            var newExe = UpdateFixture.Bytes("new-exe");
            var newHelper = UpdateFixture.Bytes("new-helper");
            await File.WriteAllBytesAsync(Path.Combine(stagingDir, "Vellum.exe"), newExe);
            await File.WriteAllBytesAsync(Path.Combine(stagingDir, "helper.dll"), newHelper);

            var request = new LauncherApplyRequest
            {
                InstallDir = installDir,
                StagingDir = stagingDir,
                BackupDir = backupDir,
                EntryPoint = "Vellum.exe",
                Version = "2.0.0",
                PreviousVersion = "1.0.0",
                Relaunch = false,
                Files =
                {
                    new ManifestFile
                    {
                        Path = "Vellum.exe", Size = newExe.Length,
                        Sha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(newExe))
                    },
                    new ManifestFile
                    {
                        Path = "helper.dll", Size = newHelper.Length,
                        Sha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(newHelper))
                    }
                },
                DeletedFiles = { "old.dll" }
            };

            var installer = new LauncherPackageInstaller(NullLogger<LauncherPackageInstaller>.Instance);
            var applied = await installer.ApplyAsync(request);
            Assert.True(applied.Success, applied.ErrorMessage);
            Assert.Equal(newExe, await File.ReadAllBytesAsync(Path.Combine(installDir, "Vellum.exe")));
            Assert.True(File.Exists(Path.Combine(installDir, "helper.dll")));
            Assert.False(File.Exists(Path.Combine(installDir, "old.dll")));

            var rolledBack = await installer.RollbackAsync(installDir, backupDir);
            Assert.True(rolledBack.Success, rolledBack.ErrorMessage);
            Assert.Equal(oldExe, await File.ReadAllBytesAsync(Path.Combine(installDir, "Vellum.exe")));
            Assert.Equal(oldData, await File.ReadAllBytesAsync(Path.Combine(installDir, "old.dll")));
            Assert.False(File.Exists(Path.Combine(installDir, "helper.dll")));
        }

        [Fact]
        public async Task Launcher_package_with_wrong_sha_is_rejected()
        {
            using var fx = new UpdateFixture();
            fx.Settings.Settings.AutoDownloadLauncherUpdates = false;
            var zip = BuildZip(new Dictionary<string, byte[]> { ["Vellum.exe"] = UpdateFixture.Bytes("x") });
            var manifest = new LauncherUpdateManifest
            {
                Version = "9.9.9",
                PackageUrl = fx.Server.Url("launcher/bad.zip"),
                PackageSize = zip.Length,
                PackageSha256 = new string('0', 64),
                Files =
                {
                    new ManifestFile
                    {
                        Path = "Vellum.exe", Size = 1, Sha256 = new string('1', 64)
                    }
                }
            };
            fx.Server.Put("launcher/bad.zip", zip);
            var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
            fx.Server.Put("launcher/bad.json", payload, fx.Sign(payload), "test-key");
            fx.Settings.Settings.LauncherUpdateManifestUrl = fx.Server.Url("launcher/bad.json");

            var service = new LauncherUpdateService(new System.Net.Http.HttpClient(), fx.Provider,
                fx.Settings, fx.Store, NullLogger<LauncherUpdateService>.Instance);
            await service.CheckForUpdatesAsync("1.0.0");
            var result = await service.DownloadAsync("1.0.0");

            Assert.False(result.Success);
            Assert.Equal(LauncherUpdatePhase.Error, service.Status.Phase);
        }

        [Fact]
        public async Task Launcher_package_connection_break_does_not_install_a_partial_package()
        {
            using var fx = new UpdateFixture();
            fx.Settings.Settings.AutoDownloadLauncherUpdates = false;
            var zip = BuildZip(new Dictionary<string, byte[]>
            {
                ["Vellum.exe"] = new byte[64 * 1024]
            });
            var packagePath = "launcher/9.9.8/truncated.zip";
            fx.Server.Put(packagePath, zip);
            fx.Server.Truncate(packagePath, Math.Max(1, zip.Length / 2));
            var manifest = new LauncherUpdateManifest
            {
                Version = "9.9.8",
                PackageUrl = fx.Server.Url(packagePath),
                PackageSize = zip.Length,
                PackageSha256 = await HashHelper.ComputeSha256Async(UpdateFixture.WriteTemp(zip)),
                Files =
                {
                    new ManifestFile
                    {
                        Path = "Vellum.exe", Size = 64 * 1024, Sha256 = new string('1', 64)
                    }
                }
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
            fx.Server.Put("launcher/9.9.8/truncated.json", payload, fx.Sign(payload), "test-key");
            fx.Settings.Settings.LauncherUpdateManifestUrl = fx.Server.Url("launcher/9.9.8/truncated.json");

            var service = new LauncherUpdateService(new System.Net.Http.HttpClient(), fx.Provider,
                fx.Settings, fx.Store, NullLogger<LauncherUpdateService>.Instance);
            await service.CheckForUpdatesAsync("1.0.0");
            var result = await service.DownloadAsync("1.0.0");

            Assert.False(result.Success);
            Assert.Equal(LauncherUpdatePhase.Error, service.Status.Phase);
            var versionDir = Path.Combine(PathHelper.LauncherUpdateDir, "9.9.8");
            Assert.False(File.Exists(Path.Combine(versionDir, "package.zip")),
                $"result={result.ErrorMessage}; status={service.Status.ErrorMessage}; files={string.Join(",", Directory.Exists(versionDir) ? Directory.GetFiles(versionDir) : Array.Empty<string>())}");
        }

        [Fact]
        public async Task Launcher_downgrade_is_not_offered()
        {
            using var fx = new UpdateFixture();
            var manifest = new LauncherUpdateManifest
            {
                Version = "0.9.0",
                PackageUrl = fx.Server.Url("launcher/old.zip"),
                PackageSize = 10,
                PackageSha256 = new string('a', 64),
                Files =
                {
                    new ManifestFile { Path = "Vellum.exe", Size = 10, Sha256 = new string('b', 64) }
                }
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
            fx.Server.Put("launcher/old.json", payload, fx.Sign(payload), "test-key");
            fx.Settings.Settings.LauncherUpdateManifestUrl = fx.Server.Url("launcher/old.json");
            var service = new LauncherUpdateService(new System.Net.Http.HttpClient(), fx.Provider,
                fx.Settings, fx.Store, NullLogger<LauncherUpdateService>.Instance);

            var status = await service.CheckForUpdatesAsync("1.0.0");

            Assert.Equal(LauncherUpdatePhase.UpToDate, status.Phase);
        }

        [Fact]
        public async Task Bootstrapper_failure_result_increments_attempts_to_prevent_loops()
        {
            using var fx = new UpdateFixture();
            await fx.Store.LoadAsync();
            fx.Store.State.Launcher = new LauncherUpdatePersistedState
            {
                OfferedVersion = "9.9.9",
                DownloadReady = true
            };
            await fx.Store.SaveAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(PathHelper.LauncherUpdateResultFile)!);
            await File.WriteAllTextAsync(PathHelper.LauncherUpdateResultFile, JsonSerializer.Serialize(
                new LauncherUpdateResultRecord
                {
                    Version = "9.9.9",
                    Success = false,
                    RolledBack = true,
                    ErrorMessage = "simulated"
                }));

            var service = new LauncherUpdateService(new System.Net.Http.HttpClient(), fx.Provider,
                fx.Settings, fx.Store, NullLogger<LauncherUpdateService>.Instance);
            var record = await service.ConsumeBootstrapperResultAsync();

            Assert.NotNull(record);
            Assert.Equal(1, fx.Store.State.Launcher!.ConsecutiveFailures);
            Assert.False(File.Exists(PathHelper.LauncherUpdateResultFile));
        }

        private static byte[] BuildZip(Dictionary<string, byte[]> files)
        {
            var path = Path.Combine(Path.GetTempPath(), "vellum-zip-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    foreach (var (name, data) in files)
                    {
                        var entry = archive.CreateEntry(name);
                        using var stream = entry.Open();
                        stream.Write(data, 0, data.Length);
                    }
                }
                return File.ReadAllBytes(path);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}


