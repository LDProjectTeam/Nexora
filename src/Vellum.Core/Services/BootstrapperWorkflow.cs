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
    /// Mandatory pre-launch sequence. The bootstrapper calls this before the main
    /// Vellum window is allowed to start:
    /// recover interrupted launcher transaction → check/apply launcher update →
    /// verify installed files → launch the launcher with the original arguments.
    /// </summary>
    public class BootstrapperWorkflow : IBootstrapperWorkflow
    {
        private readonly ILauncherUpdateService _launcherUpdates;
        private readonly LauncherPackageInstaller _packageInstaller;
        private readonly ILauncherIntegrityService _integrity;
        private readonly IProcessStarter _processStarter;
        private readonly ILogger<BootstrapperWorkflow> _logger;

        public BootstrapperWorkflow(
            ILauncherUpdateService launcherUpdates,
            LauncherPackageInstaller packageInstaller,
            ILauncherIntegrityService integrity,
            IProcessStarter processStarter,
            ILogger<BootstrapperWorkflow> logger)
        {
            _launcherUpdates = launcherUpdates;
            _packageInstaller = packageInstaller;
            _integrity = integrity;
            _processStarter = processStarter;
            _logger = logger;
        }

        public async Task<BootstrapperRunResult> RunAsync(BootstrapperRunRequest request,
            IProgress<BootstrapperProgress>? progress = null, CancellationToken ct = default)
        {
            var appDir = Path.GetFullPath(request.AppDirectory);
            var executable = Path.Combine(appDir, request.LauncherExecutableName);
            var currentVersion = string.IsNullOrWhiteSpace(request.CurrentAppVersionOverride)
                ? ReadVersion(executable)
                : request.CurrentAppVersionOverride;

            if (request.LauncherIsRunning)
            {
                Report(progress, BootstrapperStage.StartingLauncher,
                    "Vellum is already running. Bringing it to the front…");
                return Launch(request, executable, appDir, currentVersion, appliedUpdate: false, progress);
            }

            try
            {
                // 1. Recover a launcher transaction interrupted by a crash. A committed
                //    journal is a normal rollback backup and is not touched here.
                Report(progress, BootstrapperStage.CheckingIntegrity, "Checking unfinished update…");
                var recovery = await _packageInstaller.RecoverInterruptedAsync(appDir, ct, currentVersion)
                    .ConfigureAwait(false);
                if (recovery is { Success: false })
                {
                    return Fail(progress,
                        "An interrupted launcher update could not be recovered: " + recovery.ErrorMessage,
                        canRetry: true, canLaunchCurrent: false);
                }

                // 2. Check and apply a launcher update before the main window exists.
                var appliedUpdate = false;
                if (!request.SkipLauncherUpdate)
                {
                    Report(progress, BootstrapperStage.CheckingUpdates, "Checking for updates...");
                    var status = await _launcherUpdates.CheckForUpdatesAsync(
                        currentVersion, autoDownload: false, ct).ConfigureAwait(false);

                    if (status.Phase == LauncherUpdatePhase.UpToDate)
                    {
                        Report(progress, BootstrapperStage.NoUpdates, "No updates available.");
                    }
                    else if (status.Phase == LauncherUpdatePhase.UpdateAvailable
                        || status.Phase == LauncherUpdatePhase.ReadyToInstall)
                    {
                        Report(progress, BootstrapperStage.DownloadingUpdate,
                            "Downloading launcher update…", status.Fraction * 100,
                            status.DownloadedBytes, status.TotalBytes, status.SpeedBytesPerSec,
                            status.SecondsRemaining);

                        var download = OperationResult.Ok();
                        if (status.Phase != LauncherUpdatePhase.ReadyToInstall)
                        {
                            void OnStatus(object? sender, LauncherUpdateStatus updateStatus)
                            {
                                if (updateStatus.Phase is LauncherUpdatePhase.Downloading or LauncherUpdatePhase.Verifying)
                                {
                                    Report(progress, BootstrapperStage.DownloadingUpdate,
                                        updateStatus.Phase == LauncherUpdatePhase.Verifying
                                            ? "Verifying launcher update…"
                                            : "Downloading launcher update…",
                                        updateStatus.Fraction * 100, updateStatus.DownloadedBytes,
                                        updateStatus.TotalBytes, updateStatus.SpeedBytesPerSec,
                                        updateStatus.SecondsRemaining);
                                }
                            }

                            _launcherUpdates.StatusChanged += OnStatus;
                            try
                            {
                                download = await _launcherUpdates.DownloadAsync(currentVersion, ct)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                _launcherUpdates.StatusChanged -= OnStatus;
                            }
                        }
                        if (!download.Success)
                        {
                            return Fail(progress,
                                "The launcher update could not be downloaded: " +
                                (download.ErrorMessage ?? "unknown error"),
                                canRetry: true, canLaunchCurrent: true);
                        }

                        var manifest = _launcherUpdates.AvailableManifest;
                        if (manifest == null)
                            return Fail(progress, "The launcher update manifest is unavailable.",
                                canRetry: true, canLaunchCurrent: true);

                        var versionDir = Path.Combine(PathHelper.LauncherUpdateDir,
                            PathHelper.SanitizeFileName(manifest.Version));
                        var backupDir = Path.Combine(versionDir, "backup");
                        var applyRequest = _launcherUpdates.CreateApplyRequest(
                            appDir,
                            manifest.EntryPoint,
                            backupDir,
                            PathHelper.LauncherUpdateResultFile,
                            waitProcessId: 0,
                            relaunch: false);
                        applyRequest.PreviousVersion = currentVersion;

                        Report(progress, BootstrapperStage.InstallingUpdate, "Installing launcher update…");
                        var installProgress = new Progress<DownloadProgress>(p =>
                            Report(progress, BootstrapperStage.InstallingUpdate,
                                "Installing launcher update…", p.Fraction * 100,
                                p.BytesDownloaded, p.TotalBytes, 0, -1, currentFile: p.CurrentFile));

                        var apply = await _packageInstaller.ApplyAsync(
                            applyRequest, installProgress, ct).ConfigureAwait(false);
                        if (!apply.Success)
                        {
                            var afterFailure = await VerifyAsync(appDir, request.LauncherExecutableName, ct)
                                .ConfigureAwait(false);
                            return Fail(progress,
                                "The launcher update failed: " + (apply.ErrorMessage ?? "unknown error"),
                                canRetry: true, canLaunchCurrent: afterFailure.Passed);
                        }

                        appliedUpdate = true;
                        currentVersion = manifest.Version;
                    }
                    else if (status.Phase == LauncherUpdatePhase.Error
                             && !string.IsNullOrWhiteSpace(status.ErrorMessage))
                    {
                        // A check failure must not prevent the already installed launcher
                        // from starting, provided its files are intact.
                        _logger.LogWarning("Launcher update check failed: {Error}", status.ErrorMessage);
                    }
                }

                // 3. Verify required files and the last signed installed manifest.
                Report(progress, BootstrapperStage.CheckingIntegrity, "Checking integrity...");
                var integrity = await VerifyAsync(appDir, request.LauncherExecutableName, ct)
                    .ConfigureAwait(false);
                if (!integrity.Passed)
                {
                    Report(progress, BootstrapperStage.VerifyingFiles,
                        "Launcher files are damaged. Restoring the previous build…");

                    var backup = LauncherPackageInstaller.FindLatestRollbackBackup(currentVersion);
                    if (backup != null)
                    {
                        var rollback = await _packageInstaller.RollbackAsync(appDir, backup, ct)
                            .ConfigureAwait(false);
                        if (rollback.Success)
                        {
                            var afterRollback = await VerifyAsync(appDir, request.LauncherExecutableName, ct)
                                .ConfigureAwait(false);
                            if (afterRollback.Passed)
                            {
                                currentVersion = afterRollback.Version;
                                appliedUpdate = false;
                                Report(progress, BootstrapperStage.AllChecksPassed, "All checks passed.");
                                return Launch(request, executable, appDir, currentVersion, appliedUpdate, progress);
                            }
                        }
                    }

                    return Fail(progress,
                        "Vellum cannot start because required files are missing or corrupted. " +
                        string.Join(" ", integrity.Problems),
                        canRetry: true, canLaunchCurrent: false);
                }

                if (!appliedUpdate && !string.IsNullOrWhiteSpace(integrity.Version))
                    currentVersion = integrity.Version;

                Report(progress, BootstrapperStage.AllChecksPassed, "All checks passed.");
                return Launch(request, executable, appDir, currentVersion, appliedUpdate, progress);
            }
            catch (OperationCanceledException)
            {
                return Fail(progress, "The launcher start was cancelled.", canRetry: true, canLaunchCurrent: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bootstrapper workflow failed.");
                return Fail(progress, "Launcher startup failed: " + ex.Message,
                    canRetry: true, canLaunchCurrent: true);
            }
        }

        private async Task<LauncherIntegrityResult> VerifyAsync(string appDir, string executable, CancellationToken ct)
            => await _integrity.VerifyAsync(appDir, executable, ct).ConfigureAwait(false);

        private BootstrapperRunResult Launch(BootstrapperRunRequest request, string executable,
            string appDir, string version, bool appliedUpdate, IProgress<BootstrapperProgress>? progress)
        {
            Report(progress, BootstrapperStage.StartingLauncher,
                "Starting Vellum...", percent: 100, canLaunchCurrent: true);
            var arguments = request.LauncherArguments.ToList();
            string? handshakeToken = null;
            if (request.MarkBootstrappedLaunch)
            {
                arguments.RemoveAll(a => string.Equals(a, BootstrapperLaunchPolicy.LegacyMarkerArgument,
                    StringComparison.OrdinalIgnoreCase));
                if (!arguments.Contains(BootstrapperLaunchPolicy.ChildMarkerArgument,
                        StringComparer.OrdinalIgnoreCase))
                {
                    arguments.Insert(0, BootstrapperLaunchPolicy.ChildMarkerArgument);
                }
                handshakeToken = BootstrapperHandshake.CreateToken();
            }
            var start = _processStarter.Start(executable, arguments, appDir, handshakeToken);
            if (!start.Started)
            {
                return BootstrapperRunResult.Failed(
                    "Could not start Vellum: " + (start.ErrorMessage ?? "unknown error"),
                    canRetry: true, canLaunchCurrent: true);
            }

            return new BootstrapperRunResult
            {
                Success = true,
                Launched = true,
                AppliedUpdate = appliedUpdate,
                CanLaunchCurrent = true,
                CurrentVersion = version
            };
        }

        private BootstrapperRunResult Fail(IProgress<BootstrapperProgress>? progress, string message,
            bool canRetry, bool canLaunchCurrent)
        {
            _logger.LogError("Bootstrapper stopped: {Message}", message);
            Report(progress, BootstrapperStage.UpdateFailed, message,
                error: message, canRetry: canRetry, canLaunchCurrent: canLaunchCurrent);
            return BootstrapperRunResult.Failed(message, canRetry, canLaunchCurrent);
        }

        private static void Report(IProgress<BootstrapperProgress>? progress,
            BootstrapperStage stage, string message, double percent = 0,
            long bytes = 0, long total = 0, long speed = 0, long eta = -1,
            string? currentFile = null, string? error = null,
            bool canRetry = false, bool canLaunchCurrent = false)
        {
            progress?.Report(new BootstrapperProgress
            {
                Stage = stage,
                Message = message,
                Fraction = Math.Clamp(percent / 100.0, 0.0, 1.0),
                BytesDownloaded = bytes,
                TotalBytes = total,
                SpeedBytesPerSec = speed,
                SecondsRemaining = eta,
                ErrorMessage = error,
                CanRetry = canRetry,
                CanLaunchCurrent = canLaunchCurrent
            });
        }

        private static string ReadVersion(string executable)
        {
            try
            {
                return File.Exists(executable)
                    ? FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
