using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Verifies the files that must exist before the main launcher can start. Every
    /// launch checks existence/size; files described by the last signed installed
    /// manifest are additionally hashed for critical paths.
    /// </summary>
    public class LauncherIntegrityService : ILauncherIntegrityService
    {
        private static readonly string[] CriticalFiles =
        {
            "Vellum.exe",
            "Vellum.dll",
            "Vellum.Core.dll",
            "Vellum.runtimeconfig.json",
            "Vellum.deps.json"
        };

        private readonly ILogger<LauncherIntegrityService> _logger;

        public LauncherIntegrityService(ILogger<LauncherIntegrityService> logger)
        {
            _logger = logger;
        }

        public async Task<LauncherIntegrityResult> VerifyAsync(string appDirectory,
            string launcherExecutableName, CancellationToken ct = default)
        {
            var result = new LauncherIntegrityResult();
            var root = Path.GetFullPath(appDirectory);
            var state = await ReadInstalledStateAsync(ct).ConfigureAwait(false);
            result.InstalledState = state;

            var executable = Path.Combine(root, launcherExecutableName);
            if (!File.Exists(executable))
            {
                result.Problems.Add($"{launcherExecutableName} is missing.");
            }
            else
            {
                var info = new FileInfo(executable);
                if (info.Length == 0)
                    result.Problems.Add($"{launcherExecutableName} is empty.");
                try
                {
                    result.Version = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty;
                }
                catch
                {
                    // Version is diagnostic only.
                }
            }

            // An external installer (Inno/MSI) may replace the launcher without
            // updating our AppData manifest. In that case the old installed-state
            // hashes describe a different build; do not reject the freshly installed
            // launcher because of stale metadata.
            if (state != null && !string.IsNullOrWhiteSpace(state.Version)
                && !string.IsNullOrWhiteSpace(result.Version)
                && !UpdateVersion.AreSameVersion(state.Version, result.Version))
            {
                _logger.LogInformation(
                    "Ignoring launcher installed state for {StateVersion}; current EXE is {CurrentVersion}.",
                    state.Version, result.Version);
                state = null;
                result.InstalledState = null;
            }

            var required = new[] { "Vellum.dll", "Vellum.Core.dll", "Vellum.runtimeconfig.json", "Vellum.deps.json" };
            foreach (var name in required)
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path))
                {
                    result.Problems.Add($"Missing launcher file: {name}");
                    continue;
                }
                if (new FileInfo(path).Length == 0)
                    result.Problems.Add($"{name} is empty.");
            }

            if (state != null && state.Files.Count > 0)
            {
                foreach (var file in state.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!SafeRelativePath.TryResolve(root, file.Path, out var path))
                    {
                        result.Problems.Add($"Unsafe installed-state path: {file.Path}");
                        continue;
                    }

                    if (!File.Exists(path))
                    {
                        result.Problems.Add($"Missing launcher file: {file.Path}");
                        continue;
                    }

                    var length = new FileInfo(path).Length;
                    if (length != file.Size)
                    {
                        result.Problems.Add($"Wrong size: {file.Path} (expected {file.Size}, found {length})");
                        continue;
                    }

                    if (IsCritical(file.Path))
                    {
                        var actual = await HashHelper.ComputeSha256Async(path, ct).ConfigureAwait(false);
                        if (!HashHelper.HashesMatch(actual, file.Sha256))
                            result.Problems.Add($"Corrupted launcher file: {file.Path}");
                    }
                }
            }

            result.Passed = result.Problems.Count == 0;
            if (!result.Passed)
                _logger.LogError("Launcher integrity check failed: {Problems}", string.Join("; ", result.Problems));
            return result;
        }

        public Task<LauncherInstalledState?> ReadInstalledStateAsync(CancellationToken ct = default)
            => LauncherPackageInstaller.LoadInstalledStateAsync(ct);

        private static bool IsCritical(string path)
        {
            var normalized = SafeRelativePath.NormalizeRelative(path) ?? path;
            return CriticalFiles.Any(c => string.Equals(
                SafeRelativePath.NormalizeRelative(c) ?? c, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }
}
