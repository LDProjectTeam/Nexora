using System;
using System.Collections.Generic;
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
    /// Replaces the bootstrapper executable after the old process has exited. The
    /// replacement process is the already verified new executable running from a
    /// staging folder, so copying it over the target is safe. A backup is restored if
    /// anything fails; the launcher can always fall back to the previous bootstrapper.
    /// </summary>
    public class BootstrapperSelfUpdater : IBootstrapperSelfUpdater
    {
        private const string SkipSelfUpdateArgument = "--skip-self-update";
        private readonly IProcessStarter _processStarter;
        private readonly ILogger<BootstrapperSelfUpdater> _logger;

        public BootstrapperSelfUpdater(IProcessStarter processStarter,
            ILogger<BootstrapperSelfUpdater> logger)
        {
            _processStarter = processStarter;
            _logger = logger;
        }

        public async Task<BootstrapperSelfUpdateResult> ApplyAsync(BootstrapperSelfUpdateRequest request,
            IProgress<BootstrapperProgress>? progress = null, CancellationToken ct = default)
        {
            var target = Path.GetFullPath(request.TargetPath);
            var source = Path.GetFullPath(request.NewExecutablePath);
            var backup = Path.GetFullPath(request.BackupPath);

            try
            {
                if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
                    return BootstrapperSelfUpdateResult.Failed(
                        "The bootstrapper update source and target are the same file.", false);

                if (!File.Exists(source))
                    return FailAndLaunchOld(request, target, "The downloaded bootstrapper is missing.");
                if (!await VerifyFileAsync(source, request.Size, request.Sha256, ct).ConfigureAwait(false))
                    return FailAndLaunchOld(request, target,
                        "The downloaded bootstrapper failed SHA-256 verification.");

                if (request.WaitProcessId > 0)
                {
                    progress?.Report(new BootstrapperProgress
                    {
                        Stage = BootstrapperStage.InstallingUpdate,
                        Message = "Waiting for Vellum to close…"
                    });
                    await LauncherPackageInstaller.WaitForProcessExitAsync(
                        request.WaitProcessId, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                }

                var backupDir = Path.GetDirectoryName(backup);
                if (!string.IsNullOrWhiteSpace(backupDir))
                    Directory.CreateDirectory(backupDir);

                var hadTarget = File.Exists(target);
                if (hadTarget)
                {
                    File.Copy(target, backup, overwrite: true);
                    progress?.Report(new BootstrapperProgress
                    {
                        Stage = BootstrapperStage.InstallingUpdate,
                        Message = "Backing up the current bootstrapper…",
                        Fraction = 0.2
                    });
                }

                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    var temp = target + ".vellum-new-" + Guid.NewGuid().ToString("N");
                    File.Copy(source, temp, overwrite: true);
                    if (File.Exists(target))
                        File.SetAttributes(target, FileAttributes.Normal);
                    File.Move(temp, target, overwrite: true);
                }

                if (!await VerifyFileAsync(target, request.Size, request.Sha256, ct).ConfigureAwait(false))
                {
                    RestoreBackup(target, backup, hadTarget);
                    return FailAndLaunchOld(request, target,
                        "The updated bootstrapper failed verification.");
                }

                var args = WithSkipFlag(request.OriginalArguments);
                var healthFile = string.IsNullOrWhiteSpace(request.HealthFile)
                    ? null
                    : Path.GetFullPath(request.HealthFile);
                if (healthFile != null)
                {
                    TryDelete(healthFile);
                    args.Add("--bootstrapper-health");
                    args.Add(healthFile);
                }

                var launch = _processStarter.Start(target, args, targetDir ?? Environment.CurrentDirectory);
                if (!launch.Started)
                {
                    RestoreBackup(target, backup, hadTarget);
                    var oldLaunch = _processStarter.Start(target,
                        WithSkipFlag(request.OriginalArguments), targetDir ?? Environment.CurrentDirectory);
                    return BootstrapperSelfUpdateResult.Failed(
                        "The updated bootstrapper did not start; the previous bootstrapper was restored: " +
                        launch.ErrorMessage, true, oldLaunch.Started);
                }

                if (healthFile != null && !await WaitForHealthAsync(healthFile, launch.ProcessId,
                    TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
                {
                    TryKill(launch.ProcessId);
                    RestoreBackup(target, backup, hadTarget);
                    var oldLaunch = _processStarter.Start(target,
                        WithSkipFlag(request.OriginalArguments), targetDir ?? Environment.CurrentDirectory);
                    return BootstrapperSelfUpdateResult.Failed(
                        "The updated bootstrapper did not report a healthy start; the previous bootstrapper was restored.",
                        true, oldLaunch.Started);
                }

                if (healthFile != null)
                    TryDelete(healthFile);
                progress?.Report(new BootstrapperProgress
                {
                    Stage = BootstrapperStage.StartingLauncher,
                    Message = "Starting the updated bootstrapper…",
                    Fraction = 1
                });
                return BootstrapperSelfUpdateResult.Ok(true);
            }
            catch (OperationCanceledException)
            {
                return FailAndLaunchOld(request, target, "Bootstrapper self-update was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bootstrapper self-update failed.");
                return FailAndLaunchOld(request, target,
                    "Bootstrapper self-update failed: " + ex.Message);
            }
        }

        private BootstrapperSelfUpdateResult FailAndLaunchOld(BootstrapperSelfUpdateRequest request,
            string target, string message)
        {
            try
            {
                RestoreBackup(target, Path.GetFullPath(request.BackupPath),
                    File.Exists(Path.GetFullPath(request.BackupPath)));
                var args = WithSkipFlag(request.OriginalArguments);
                var directory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory;
                var launched = File.Exists(target)
                    && _processStarter.Start(target, args, directory).Started;
                if (launched)
                    return BootstrapperSelfUpdateResult.Failed(message, true, true);

                // Last resort: never leave the user with no Vellum at all.
                if (!string.IsNullOrWhiteSpace(request.LauncherExecutablePath)
                    && File.Exists(request.LauncherExecutablePath))
                {
                    var directArgs = WithBootstrappedFlag(args);
                    var token = BootstrapperHandshake.CreateToken();
                    var direct = _processStarter.Start(request.LauncherExecutablePath, directArgs,
                        Path.GetDirectoryName(request.LauncherExecutablePath)!, token);
                    return BootstrapperSelfUpdateResult.Failed(message, true, direct.Started);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not restart the previous bootstrapper.");
            }

            return BootstrapperSelfUpdateResult.Failed(message, true, false);
        }

        private static void RestoreBackup(string target, string backup, bool hadTarget)
        {
            try
            {
                if (hadTarget && File.Exists(backup))
                {
                    var directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    if (File.Exists(target))
                        File.SetAttributes(target, FileAttributes.Normal);
                    File.Copy(backup, target, overwrite: true);
                }
            }
            catch
            {
                // Best effort; the caller still tries to start a usable executable.
            }
        }

        private static List<string> WithSkipFlag(IEnumerable<string> args)
        {
            var list = args.Where(a => !string.Equals(a, SkipSelfUpdateArgument,
                StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, SkipSelfUpdateArgument);
            return list;
        }

        private static List<string> WithBootstrappedFlag(IEnumerable<string> args)
        {
            var list = args.Where(a => !string.Equals(a, BootstrapperLaunchPolicy.LegacyMarkerArgument,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(a, BootstrapperLaunchPolicy.ChildMarkerArgument,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, BootstrapperLaunchPolicy.ChildMarkerArgument);
            return list;
        }

        private static async Task<bool> WaitForHealthAsync(string path, int? processId,
            TimeSpan timeout, CancellationToken ct)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(path))
                    return true;

                if (processId.HasValue)
                {
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
                        if (process.HasExited)
                            return false;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            return File.Exists(path);
        }

        private static void TryKill(int? processId)
        {
            if (!processId.HasValue)
                return;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process already exited or cannot be controlled.
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static async Task<bool> VerifyFileAsync(string path, long size, string sha256,
            CancellationToken ct)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size)
                return false;
            var actual = await HashHelper.ComputeSha256Async(path, ct).ConfigureAwait(false);
            return HashHelper.HashesMatch(actual, sha256);
        }
    }
}
