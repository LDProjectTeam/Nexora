using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    public class LauncherIntegrityTests
    {
        [Fact]
        public async Task Installed_state_from_an_external_installer_is_not_treated_as_corruption()
        {
            var root = Path.Combine(Path.GetTempPath(), "VellumIntegrity",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var vellumExe = Path.Combine(root, "Vellum.exe");
            var testHost = Environment.ProcessPath!;
            File.Copy(testHost, vellumExe);
            CreateRequiredFiles(root);
            var currentVersion = FileVersionInfo.GetVersionInfo(vellumExe).FileVersion ?? "1.0.0";
            var oldStatePath = PathHelper.LauncherInstalledStateFile;
            var backupPath = oldStatePath + ".audit-test";
            try
            {
                PathHelper.EnsureDirectory(PathHelper.LauncherUpdateDir);
                if (File.Exists(oldStatePath))
                    File.Copy(oldStatePath, backupPath, overwrite: true);

                var staleState = new LauncherInstalledState
                {
                    Version = "0.0.1",
                    Files =
                    {
                        new ManifestFile
                        {
                            Path = "Vellum.exe",
                            Size = new FileInfo(vellumExe).Length,
                            Sha256 = new string('0', 64)
                        }
                    }
                };
                File.WriteAllText(oldStatePath, JsonSerializer.Serialize(staleState,
                    new JsonSerializerOptions { WriteIndented = true }));

                var service = new LauncherIntegrityService(
                    NullLogger<LauncherIntegrityService>.Instance);
                var result = await service.VerifyAsync(root, "Vellum.exe");

                Assert.True(result.Passed);
                Assert.Null(result.InstalledState);
                Assert.False(string.IsNullOrWhiteSpace(result.Version));
                Assert.NotEqual("0.0.1", result.Version);
                _ = currentVersion;
            }
            finally
            {
                try
                {
                    if (File.Exists(backupPath))
                        File.Move(backupPath, oldStatePath, overwrite: true);
                    else if (File.Exists(oldStatePath))
                        File.Delete(oldStatePath);
                }
                catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public async Task Matching_installed_state_still_detects_a_corrupted_launcher_exe()
        {
            var root = Path.Combine(Path.GetTempPath(), "VellumIntegrity",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var vellumExe = Path.Combine(root, "Vellum.exe");
            File.Copy(Environment.ProcessPath!, vellumExe);
            CreateRequiredFiles(root);
            var version = FileVersionInfo.GetVersionInfo(vellumExe).FileVersion ?? "1.0.0";
            var oldStatePath = PathHelper.LauncherInstalledStateFile;
            var backupPath = oldStatePath + ".audit-test-2";
            try
            {
                PathHelper.EnsureDirectory(PathHelper.LauncherUpdateDir);
                if (File.Exists(oldStatePath))
                    File.Copy(oldStatePath, backupPath, overwrite: true);

                var state = new LauncherInstalledState
                {
                    Version = version,
                    Files =
                    {
                        new ManifestFile
                        {
                            Path = "Vellum.exe",
                            Size = new FileInfo(vellumExe).Length,
                            Sha256 = new string('f', 64)
                        }
                    }
                };
                File.WriteAllText(oldStatePath, JsonSerializer.Serialize(state));

                var service = new LauncherIntegrityService(
                    NullLogger<LauncherIntegrityService>.Instance);
                var result = await service.VerifyAsync(root, "Vellum.exe");

                Assert.False(result.Passed);
                Assert.Contains(result.Problems, problem => problem.Contains("Corrupted", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                try
                {
                    if (File.Exists(backupPath))
                        File.Move(backupPath, oldStatePath, overwrite: true);
                    else if (File.Exists(oldStatePath))
                        File.Delete(oldStatePath);
                }
                catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }
        private static void CreateRequiredFiles(string root)
        {
            File.WriteAllText(Path.Combine(root, "Vellum.dll"), "dll");
            File.WriteAllText(Path.Combine(root, "Vellum.Core.dll"), "core");
            File.WriteAllText(Path.Combine(root, "Vellum.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(root, "Vellum.deps.json"), "{}");
        }
    }
}
