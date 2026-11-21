using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Installs a downloaded game from the staging folder to the final path.
    /// Supported staging contents:
    ///  1. Portable folder (torrent output)            -> moved as is
    ///  2. One or several archives (zip / 7z / rar,
    ///     including multi-part rar)                   -> extracted and merged
    ///  3. ISO images                                  -> mounted, then setup.exe is run
    ///  4. setup.exe (no archives):
    ///       - Inno Setup / NSIS (auto-detected)       -> silent install into target
    ///       - custom args from the catalog ({DIR})    -> silent install with those args
    ///       - unknown custom installer                -> its window is shown, user installs
    ///         into the target folder shown in the progress text
    /// Requires NuGet package: SharpCompress.
    /// </summary>
    public class Installer : IInstaller
    {
        private const int BufferSize = 1 * 1024 * 1024; // 1 MB — large buffer for fast disk I/O during extraction
        private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };
        private static readonly TimeSpan IsoOperationTimeout = TimeSpan.FromSeconds(45);

        private readonly ILogger<Installer> _logger;

        public Installer(ILogger<Installer> logger)
        {
            _logger = logger;
        }

        public async Task<OperationResult> InstallFromStagingAsync(
            string gameId,
            string stagingPath,
            string targetPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default,
            InstallOptions? options = null)
        {
            string? extractDir = null;

            try
            {
                _logger.LogInformation("Installing {GameId} from {Staging} to {Target}",
                    gameId, stagingPath, targetPath);

                if (!Directory.Exists(stagingPath))
                    return OperationResult.Fail($"Staging folder not found: {stagingPath}");

                if (options?.IsRepack == true
                    && GameExecutableLocator.FindMainExecutable(targetPath) == null
                    && FindMatchingSiblingInstall(targetPath) is { } existingInstallPath)
                {
                    _logger.LogInformation(
                        "Found completed repack installation for {GameId} at {InstallPath}; adopting it instead of running setup again",
                        gameId, existingInstallPath);
                    CleanupGameStaging(gameId, stagingPath);
                    TryCleanupAbandonedInstallTarget(targetPath);
                    Report(progress, gameId, "Installing", 1.0, existingInstallPath);
                    return OperationResult.Ok(existingInstallPath);
                }

                Report(progress, gameId, "Installing", 0);

                string sourceDir = stagingPath;
                var archives = FindArchives(stagingPath);

                if (archives.Count > 0)
                {
                    // Extract into a separate folder so staging stays intact if something fails.
                    extractDir = stagingPath.TrimEnd(Path.DirectorySeparatorChar) + "._extract";
                    DeleteDirectorySafe(extractDir);
                    Directory.CreateDirectory(extractDir);

                    for (int i = 0; i < archives.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        int index = i;
                        var archivePath = archives[i];
                        _logger.LogInformation("Extracting {Archive} ({N}/{Total})",
                            archivePath, i + 1, archives.Count);

                        await ExtractArchiveAsync(archivePath, extractDir, ct, (frac, file) =>
                            Report(progress, gameId, "Extracting",
                                (index + frac) / archives.Count, file)).ConfigureAwait(false);
                    }

                    sourceDir = FlattenSingleRoot(extractDir);
                }

                var setupExe = FindSetupExe(sourceDir, options);
                if (setupExe != null)
                    return await RunSetupAsync(gameId, setupExe, Path.GetDirectoryName(setupExe)!, stagingPath, targetPath,
                        options, progress, ct).ConfigureAwait(false);

                var isoImages = FindIsoImages(sourceDir);
                foreach (var isoPath in isoImages)
                {
                    ct.ThrowIfCancellationRequested();
                    var mounted = false;
                    OperationResult? isoResult = null;
                    try
                    {
                        Report(progress, gameId, "Mounting image", 0, Path.GetFileName(isoPath));
                        var mountedRoot = await MountIsoAsync(isoPath, ct).ConfigureAwait(false);
                        mounted = true;
                        var isoSetup = FindSetupExe(mountedRoot, options);
                        if (isoSetup == null)
                        {
                            _logger.LogWarning("No setup executable found in mounted ISO {IsoPath}", isoPath);
                            continue;
                        }

                        isoResult = await RunSetupAsync(gameId, isoSetup, Path.GetDirectoryName(isoSetup)!,
                            stagingPath, targetPath, options, progress, ct, cleanupStaging: false).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not install from ISO {IsoPath}", isoPath);
                    }
                    finally
                    {
                        if (mounted)
                            await DismountIsoAsync(isoPath).ConfigureAwait(false);
                    }
                    if (isoResult != null)
                    {
                        if (isoResult.Success)
                            CleanupGameStaging(gameId, stagingPath);
                        return isoResult;
                    }
                }

                sourceDir = FlattenSingleRoot(sourceDir);

                if (isoImages.Count > 0)
                    return OperationResult.Fail("Installer (setup.exe) was not found in the downloaded archives or ISO images.");

                if (options?.IsRepack == true)
                    return OperationResult.Fail("Repack installer (setup.exe) was not found in the downloaded files.");

                if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
                    return OperationResult.Fail("Nothing to install: the folder is empty.");

                await ReplaceTargetAsync(gameId, sourceDir, targetPath, progress, ct).ConfigureAwait(false);

                // Remove only this game's staging directory, leaving concurrent downloads untouched.
                CleanupGameStaging(gameId, stagingPath);

                Report(progress, gameId, "Installing", 1.0);
                _logger.LogInformation("{GameId} installed successfully to {Target}", gameId, targetPath);
                return OperationResult.Ok();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Installation of {GameId} cancelled.", gameId);
                return OperationResult.Fail("Installation cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Installation failed for {GameId}", gameId);
                return OperationResult.Fail($"Installation failed: {ex.Message}", ex);
            }
            finally
            {
                if (extractDir != null)
                    DeleteDirectorySafe(extractDir);
            }
        }

        public Task<OperationResult> UninstallAsync(string gameId, string installPath)
        {
            try
            {
                if (!Directory.Exists(installPath))
                    return Task.FromResult(OperationResult.Ok());

                if (!PathHelper.IsSafeToDeleteTree(installPath))
                {
                    _logger.LogError("Refusing to uninstall {GameId}: suspicious install path {Path}", gameId, installPath);
                    return Task.FromResult(OperationResult.Fail(Localizer.T("DeleteUnsafePath",
                        "Удаление отменено: путь выглядит подозрительно (корень диска или системная папка).")));
                }

                _logger.LogInformation("Uninstalling {GameId} from {Path}", gameId, installPath);
                DeleteDirectory(installPath);
                _logger.LogInformation("{GameId} uninstalled.", gameId);
                return Task.FromResult(OperationResult.Ok());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Uninstall failed for {GameId}", gameId);
                return Task.FromResult(OperationResult.Fail($"Uninstall failed: {ex.Message}", ex));
            }
        }

        // ---------------------------------------------------------------- detection

        /// <summary>Top-level archives, first volumes only, sorted by name.</summary>
        private static List<string> FindArchives(string folder)
        {
            return Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => ArchiveExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(IsFirstVolume)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> FindIsoImages(string folder)
        {
            if (!Directory.Exists(folder))
                return new List<string>();

            return Directory.GetFiles(folder, "*.iso", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<string> MountIsoAsync(string isoPath, CancellationToken ct)
        {
            var escapedPath = Path.GetFullPath(isoPath).Replace("'", "''", StringComparison.Ordinal);
            var script = "$ErrorActionPreference = 'Stop'; " +
                "$disk = Mount-DiskImage -ImagePath '" + escapedPath + "' -PassThru -ErrorAction Stop; " +
                "$volume = $disk | Get-Volume | Where-Object { $_.DriveLetter } | Select-Object -First 1; " +
                "if ($null -eq $volume) { throw 'The ISO image has no mounted drive letter.' }; " +
                "Write-Output ($volume.DriveLetter + ':\\')";

            var output = await RunPowerShellAsync(script, ct).ConfigureAwait(false);
            var driveRoot = output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (driveRoot == null || !Regex.IsMatch(driveRoot, @"^[A-Za-z]:\\$"))
                throw new InvalidOperationException("Windows mounted the ISO but did not return a valid drive path.");

            return driveRoot;
        }

        private async Task DismountIsoAsync(string isoPath)
        {
            try
            {
                var escapedPath = Path.GetFullPath(isoPath).Replace("'", "''", StringComparison.Ordinal);
                await RunPowerShellAsync(
                    "$ErrorActionPreference = 'Stop'; Dismount-DiskImage -ImagePath '" + escapedPath + "' -ErrorAction Stop",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not dismount ISO image {IsoPath}", isoPath);
            }
        }

        private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(IsoOperationTimeout);
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
                throw new InvalidOperationException("Could not start Windows PowerShell to mount the ISO image.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested)
                    throw;
                throw new TimeoutException("Windows ISO mount operation timed out.");
            }

            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"Windows PowerShell exited with code {process.ExitCode}."
                    : error.Trim());
            return output;
        }

        // game.part1.rar / game.part01.rar -> first volume; game.part2.rar -> skip (opened automatically)
        private static bool IsFirstVolume(string path)
        {
            var name = Path.GetFileName(path);
            var m = Regex.Match(name, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
            return !m.Success || int.Parse(m.Groups[1].Value) == 1;
        }

        /// <summary>If the folder contains only one subfolder and no files, use that subfolder.</summary>
        private static string FlattenSingleRoot(string folder)
        {
            var dirs = Directory.GetDirectories(folder);
            var files = Directory.GetFiles(folder);
            return dirs.Length == 1 && files.Length == 0 ? dirs[0] : folder;
        }

        // ---------------------------------------------------------------- setup.exe

        private enum InstallerKind { Unknown, Inno, Nsis }

        private static readonly byte[][] InnoMarkers =
        {
            Encoding.ASCII.GetBytes("Inno Setup"),
            Encoding.ASCII.GetBytes("InnoSetupLdrWindow")
        };
        private static readonly byte[] NsisMarker = Encoding.ASCII.GetBytes("Nullsoft");

        /// <summary>Looks for installer signatures in the first 4 MB of the exe (the loader stub).</summary>
        private static InstallerKind DetectInstallerKind(string exePath)
        {
            try
            {
                int len = (int)Math.Min(new FileInfo(exePath).Length, 4 * 1024 * 1024);
                var buffer = new byte[len];
                int total = 0;

                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n;
                    while (total < len && (n = fs.Read(buffer, total, len - total)) > 0)
                        total += n;
                }

                var span = new ReadOnlySpan<byte>(buffer, 0, total);

                foreach (var marker in InnoMarkers)
                    if (span.IndexOf(new ReadOnlySpan<byte>(marker)) >= 0)
                        return InstallerKind.Inno;

                if (span.IndexOf(new ReadOnlySpan<byte>(NsisMarker)) >= 0)
                    return InstallerKind.Nsis;
            }
            catch
            {
                // Unreadable file: treat as unknown.
            }

            return InstallerKind.Unknown;
        }

        /// <summary>
        /// Finds the installer exe in the top level of the folder, or null if this is not an installer package.
        /// A portable game that merely ships a "Setup.exe" config tool must NOT be treated as an installer.
        /// </summary>
        private static string? FindSetupExe(string folder, InstallOptions? options)
        {
            bool explicitRequest = options != null
                && (options.Mode == InstallerMode.Interactive || options.IsRepack
                    || !string.IsNullOrWhiteSpace(options.InstallerArgs));

            // Prefer the package's root setup.exe. Repack installers often bundle several
            // helper executables under Redist/Support; a recursive scan can otherwise see
            // multiple candidates and incorrectly report that no installer was found.
            if (explicitRequest)
            {
                var rootSetup = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => string.Equals(Path.GetFileName(f), "setup.exe", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (rootSetup.Length == 1)
                    return rootSetup[0];

                var nestedSetup = Directory.GetFiles(folder, "setup.exe", SearchOption.AllDirectories);
                if (nestedSetup.Length == 1)
                    return nestedSetup[0];
            }

            var searchOption = explicitRequest ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var exes = Directory.GetFiles(folder, "*.exe", searchOption);

            var candidates = exes.Where(f =>
            {
                var name = Path.GetFileName(f);
                return !name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                       && (name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("install", StringComparison.OrdinalIgnoreCase));
            }).ToList();

            // Catalog says "this is an installer": accept the only exe even if it has an unusual name.
            if (explicitRequest)
                return candidates.Count == 1 ? candidates[0] : exes.Length == 1 ? exes[0] : null;

            if (candidates.Count != 1)
                return null;

            var exe = candidates[0];

            if (DetectInstallerKind(exe) != InstallerKind.Unknown)
                return exe;

            // Unknown (custom) installer: only if it is the single exe in the package.
            return exes.Length == 1 ? exe : null;
        }

        private async Task<OperationResult> RunSetupAsync(
            string gameId, string setupExe, string workingDirectory, string stagingPath, string targetPath,
            InstallOptions? options, IProgress<DownloadProgress>? progress, CancellationToken ct,
            bool cleanupStaging = true)
        {
            var fullTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar);
            var kind = DetectInstallerKind(setupExe);

            string? args = null;
            if (options?.Mode == InstallerMode.Interactive)
            {
                // Keep the installer interactive, but preselect the folder tracked by the launcher.
                args = kind switch
                {
                    InstallerKind.Inno => $"/DIR=\"{fullTarget}\"",
                    InstallerKind.Nsis => $"/D={fullTarget}",
                    _ => null
                };
            }
            else if (!string.IsNullOrWhiteSpace(options?.InstallerArgs))
            {
                args = options!.InstallerArgs!.Replace("{DIR}", fullTarget, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                args = kind switch
                {
                    InstallerKind.Inno => $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR=\"{fullTarget}\"",
                    InstallerKind.Nsis => $"/S /D={fullTarget}", // /D must be last and unquoted
                    _ => null
                };
            }

            bool silent = options?.Mode != InstallerMode.Interactive && args != null;
            _logger.LogInformation(
                "Running {Mode} installer for {GameId}: executable={Setup}, kind={Kind}, arguments={Arguments}, workingDirectory={WorkingDirectory}, expectedInstallPath={Target}",
                silent ? "silent" : "interactive", gameId, setupExe, kind, args, workingDirectory, fullTarget);

            // Running an executable from a downloaded repack is a security boundary:
            // ask once, with the exact path/size/mode, before executing it.
            if (!EnsureInstallerConsent(gameId, setupExe, elevated: false, silent, "setup"))
            {
                _logger.LogInformation("User declined to run the installer for {GameId}.", gameId);
                return OperationResult.Fail(Localizer.T("InstallationCancelledByUser",
                    "Установка отменена пользователем."));
            }

            Directory.CreateDirectory(fullTarget);

            // Before running the main setup, silently install any bundled VCRedist prerequisites
            // that live in the Redist/ subfolder of the staging directory (common in repacks).
            await InstallBundledRedistAsync(workingDirectory, progress, gameId, ct).ConfigureAwait(false);

            if (silent)
                Report(progress, gameId, "Running installer", 0, Path.GetFileName(setupExe));
            else
                Report(progress, gameId, "Waiting for installer", 0, $"Install to: {fullTarget}");

            // The torrent engine should have been stopped before we get here, but file
            // handles may not be released instantly.  Retry a few times with a short delay
            // so we don't fail with "The process cannot access the file because it is
            // being used by another process".
            Process? process = null;
            const int maxStartAttempts = 5;
            for (int attempt = 1; attempt <= maxStartAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = setupExe,
                        Arguments = args ?? string.Empty,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = true // lets Windows show UAC if the installer needs it
                    });
                    break; // started successfully
                }
                catch (System.ComponentModel.Win32Exception ex) when (attempt < maxStartAttempts)
                {
                    _logger.LogWarning(
                        "Attempt {Attempt}/{Max} to start installer for {GameId} failed: {Message}. Retrying in {Delay}ms…",
                        attempt, maxStartAttempts, gameId, ex.Message, attempt * 1000);
                    await Task.Delay(attempt * 1000, ct).ConfigureAwait(false);
                }
            }

            if (process == null)
                return OperationResult.Fail("Could not start the installer.");

            using (process)
            try
            {
                // When UseShellExecute=true the returned Process object may not be
                // associated with a real OS process (e.g. the user cancelled UAC, or
                // ShellExecute handed off to an existing process via DDE/COM).
                // In that case WaitForExitAsync throws InvalidOperationException
                // "No process is associated with this object."
                // Guard against it: if HasExited throws, the handle is stale.
                try
                {
                    if (process.HasExited)
                    {
                        _logger.LogInformation(
                            "Installer process for {GameId} already exited before WaitForExit (exit code unavailable via shell).",
                            gameId);
                        // Cannot read ExitCode reliably — fall through to installerSucceeded
                        // and let the post-install validation decide whether files are present.
                        goto installerSucceeded;
                    }
                }
                catch (InvalidOperationException)
                {
                    _logger.LogWarning(
                        "Installer process for {GameId} has no associated OS process (UAC cancelled or shell handoff). Treating as failure.",
                        gameId);
                    return OperationResult.Fail(
                        Localizer.T("InstallerNoProcess",
                            "Не удалось запустить установщик. Возможно, запрос на повышение прав (UAC) был отклонён. Попробуйте запустить лаунчер от имени администратора."));
                }

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // WaitForExitAsync itself can also throw if the process handle becomes
                // invalid mid-wait (rare race with ShellExecute processes).
                _logger.LogWarning(ex,
                    "WaitForExitAsync threw InvalidOperationException for {GameId}. The installer may have been handed off by the shell.",
                    gameId);
                return OperationResult.Fail(
                    Localizer.T("InstallerNoProcess",
                        "Не удалось запустить установщик. Возможно, запрос на повышение прав (UAC) был отклонён. Попробуйте запустить лаунчер от имени администратора."));
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { /* ignore */ }
                throw;
            }

            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // ShellExecute process — handle became invalid before we could read
                // the exit code. Treat as success and let post-install validation
                // (GameExecutableLocator) decide whether files are actually present.
                _logger.LogWarning(
                    "Could not read exit code for {GameId} (process handle invalid). Assuming success.",
                    gameId);
                goto installerSucceeded;
            }

            _logger.LogInformation(
                "Installer exited for {GameId} with code {ExitCode}; expected install path was {Target}",
                gameId, exitCode, fullTarget);
            if (exitCode != 0)
            {
                // In interactive mode the user may press "Cancel" inside the installer window.
                // Inno Setup and NSIS both return exit code 1 on user-cancel; treat this as a
                // graceful cancellation rather than a hard error so the UI does not show
                // "Installer exited with code 1" when the user deliberately aborts.
                bool isInteractive = options?.Mode == InstallerMode.Interactive;
                bool isCancelCode  = exitCode == 1                       // NSIS cancel / Inno abort
                                  || exitCode == 2                       // Inno Setup "abort"
                                  || exitCode == 3;                     // Inno "install aborted"

                if (isInteractive && isCancelCode)
                {
                    _logger.LogInformation(
                        "Interactive installer for {GameId} was cancelled by the user (exit code {ExitCode}).",
                        gameId, exitCode);
                    TryCleanupAbandonedInstallTarget(fullTarget);
                    return OperationResult.Fail(Localizer.T("InstallationCancelledByUser", "Установка отменена пользователем."));
                }

                // 0xE06D7363 (-529697949) is the Windows SEH code for an unhandled C++ exception —
                // the installer process crashed. Common causes for repacks:
                //   • Missing Visual C++ Redistributable (2013/2015/2019)
                //   • Installer needs administrator privileges but wasn't elevated
                // Strategy:
                //   1. If the first attempt was silent, retry interactively (show UI + any error dialogs).
                //   2. Regardless of the first attempt mode, retry once more with explicit "runas"
                //      elevation so Windows grants the installer administrator rights.
                const int CppSehExceptionCode = unchecked((int)0xE06D7363);
                if (exitCode == CppSehExceptionCode)
                {
                    // Step 1: if silent, retry without silent args to let the installer show its UI.
                    if (silent)
                    {
                        _logger.LogWarning(
                            "Silent installer for {GameId} crashed (0xE06D7363). Retrying interactively.",
                            gameId);

                        Report(progress, gameId, "Waiting for installer", 0, $"Install to: {fullTarget}");

                        var interactiveArgs = kind switch
                        {
                            InstallerKind.Inno => $"/DIR=\"{fullTarget}\"",
                            InstallerKind.Nsis => $"/D={fullTarget}",
                            _ => string.Empty
                        };

                        var interactiveResult = await RunElevatedOrShellAsync(
                            setupExe, interactiveArgs, workingDirectory, false, ct).ConfigureAwait(false);

                        if (interactiveResult == 0)
                            goto installerSucceeded;

                        if (interactiveResult == 1 || interactiveResult == 2 || interactiveResult == 3)
                            return OperationResult.Fail(Localizer.T("InstallationCancelledByUser", "Установка отменена пользователем."));

                        if (interactiveResult != CppSehExceptionCode)
                            return OperationResult.Fail($"Installer exited with code {interactiveResult}.");

                        // Still crashing — fall through to the elevation retry.
                    }

                    // Step 2: retry with administrator elevation via "runas" shell verb.
                    _logger.LogWarning(
                        "Installer for {GameId} crashed (0xE06D7363). Retrying with administrator elevation (runas).",
                        gameId);

                    Report(progress, gameId, "Waiting for installer (as Administrator)", 0, $"Install to: {fullTarget}");

                    if (!EnsureInstallerConsent(gameId, setupExe, elevated: true, silent: false, "elevation-retry"))
                    {
                        _logger.LogInformation("User declined the elevated installer retry for {GameId}.", gameId);
                        return OperationResult.Fail(Localizer.T("InstallationCancelledByUser",
                            "Установка отменена пользователем."));
                    }

                    var elevatedArgs = kind switch
                    {
                        InstallerKind.Inno => $"/DIR=\"{fullTarget}\"",
                        InstallerKind.Nsis => $"/D={fullTarget}",
                        _ => string.Empty
                    };

                    var elevatedResult = await RunElevatedOrShellAsync(
                        setupExe, elevatedArgs, workingDirectory, true, ct).ConfigureAwait(false);

                    if (elevatedResult == 0)
                        goto installerSucceeded;

                    if (elevatedResult == 1 || elevatedResult == 2 || elevatedResult == 3)
                        return OperationResult.Fail(Localizer.T("InstallationCancelledByUser", "Установка отменена пользователем."));

                    if (elevatedResult == CppSehExceptionCode)
                    {
                        // Both normal and elevated runs crashed with a C++ SEH exception.
                        // This often happens with Xatab/other repacks that use old Inno Setup
                        // skin DLLs (botva2.dll, ISSkin.dll) that crash on modern Windows.
                        // Fall back to direct FreeArc extraction which bypasses setup.tmp entirely.
                        _logger.LogWarning(
                            "Installer for {GameId} crashed twice (0xE06D7363). Attempting FreeArc direct extraction fallback.",
                            gameId);

                        Report(progress, gameId, "Extracting game files directly (installer crashed)", 0, null);

                        var arcResult = await TryFreeArcExtractionAsync(
                            gameId, setupExe, workingDirectory, fullTarget, progress, ct).ConfigureAwait(false);

                        if (arcResult.Success)
                            goto installerSucceeded;

                        // FreeArc fallback also failed – surface the most helpful error
                        return OperationResult.Fail(
                            Localizer.T("InstallerCrashedVcRedist",
                                "Установщик репака завершился с ошибкой 0xE06D7363. Установите Visual C++ Redistributable 2013/2015/2019 (x86 и x64) и повторите попытку."));
                    }

                    return OperationResult.Fail($"Installer exited with code {elevatedResult}.");
                }

                string hint = exitCode switch
                {
                    1 => " (installer error or silent-mode cancellation)",
                    2 => " (installer reported a fatal error)",
                    _ => string.Empty
                };
                return OperationResult.Fail($"Installer exited with code {exitCode}{hint}.");
            }

            installerSucceeded:

            // Some repacks add their own title-named folder or ignore the launcher's initial path.
            // Accept a uniquely matching sibling only when it contains a real game executable.
            var actualInstallPath = GameExecutableLocator.FindMainExecutable(fullTarget) != null
                ? fullTarget
                : FindMatchingSiblingInstall(fullTarget);
            if (actualInstallPath == null)
                return OperationResult.Fail(
                    string.Format(Localizer.T("InstallerFinishedButGameNotFound",
                        "Установщик завершился, но игра не найдена в {0} или в одноимённой папке рядом. Установщик: {1}. Проверьте выбранный путь и повторите установку."),
                        fullTarget, setupExe));

            if (!string.Equals(actualInstallPath, fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Repack installer for {GameId} installed outside the requested path. Tracking actual path {InstallPath}",
                    gameId, actualInstallPath);
                TryCleanupAbandonedInstallTarget(fullTarget);
            }

            if (cleanupStaging)
                CleanupGameStaging(gameId, stagingPath);
            Report(progress, gameId, "Installing", 1.0, actualInstallPath);
            return OperationResult.Ok(actualInstallPath);
        }

        private void TryCleanupAbandonedInstallTarget(string targetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
                    return;

                if (!Directory.EnumerateFileSystemEntries(targetPath).Any()
                    || GameExecutableLocator.FindMainExecutable(targetPath) == null)
                {
                    PathHelper.DeleteEmptyDirectory(targetPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clean up abandoned install target {Path}", targetPath);
            }
        }

        private static string? FindMatchingSiblingInstall(string targetPath)
        {
            try
            {
                var fullTarget = Path.GetFullPath(targetPath);
                var parent = Path.GetDirectoryName(fullTarget);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    return null;

                var targetName = NormalizeDirectoryName(Path.GetFileName(fullTarget));
                if (targetName.Length == 0)
                    return null;

                var matches = Directory.GetDirectories(parent)
                    .Where(path => !string.Equals(Path.GetFullPath(path), fullTarget,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(path => string.Equals(
                        NormalizeDirectoryName(Path.GetFileName(path)), targetName,
                        StringComparison.Ordinal))
                    .Where(path => GameExecutableLocator.FindMainExecutable(path) != null)
                    .Take(2)
                    .ToList();

                return matches.Count == 1 ? matches[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeDirectoryName(string name)
            => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // ---------------------------------------------------------------- extraction

        private Task ExtractArchiveAsync(
            string archivePath, string destDir, CancellationToken ct, Action<double, string> report)
        {
            return Task.Run(() =>
            {
                var sep = Path.DirectorySeparatorChar;
                var destRoot = Path.GetFullPath(destDir).TrimEnd(sep) + sep;

                using var archive = ArchiveFactory.OpenArchive(archivePath);

                long totalBytes = archive.Entries.Where(e => !e.IsDirectory).Sum(e => Math.Max(e.Size, 0));
                long doneBytes = 0;
                var clock = Stopwatch.StartNew();
                var buffer = new byte[BufferSize];

                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var key = entry.Key;
                    if (string.IsNullOrEmpty(key))
                        continue;

                    var outPath = Path.GetFullPath(Path.Combine(destDir, key));

                    // Zip Slip protection: the result must stay inside destDir.
                    if (!outPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Unsafe path in archive: {key}");

                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(outPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                    using (var src = entry.OpenEntryStream())
                    using (var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                               FileShare.None, BufferSize))
                    {
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            dst.Write(buffer, 0, read);
                            doneBytes += read;

                            if (clock.ElapsedMilliseconds >= 200)
                            {
                                report(totalBytes > 0 ? (double)doneBytes / totalBytes : 0, key);
                                clock.Restart();
                            }
                        }
                    }
                }

                report(1.0, Path.GetFileName(archivePath));
            }, ct);
        }

        // ---------------------------------------------------------------- move to target

        private async Task ReplaceTargetAsync(
            string gameId, string sourceDir, string targetPath,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            string? backupPath = null;

            if (Directory.Exists(targetPath))
            {
                backupPath = targetPath + "._backup";
                _logger.LogInformation("Backing up existing install to {Backup}", backupPath);
                DeleteDirectorySafe(backupPath);
                Directory.Move(targetPath, backupPath);
            }

            try
            {
                await MoveDirectoryAsync(gameId, sourceDir, targetPath, progress, ct).ConfigureAwait(false);
            }
            catch
            {
                _logger.LogError("Move failed, restoring backup.");

                // Remove a partially copied target first, otherwise the backup can't be restored.
                DeleteDirectorySafe(targetPath);
                if (backupPath != null && Directory.Exists(backupPath))
                    Directory.Move(backupPath, targetPath);
                throw;
            }

            if (backupPath != null)
                DeleteDirectorySafe(backupPath);
        }

        private async Task MoveDirectoryAsync(
            string gameId, string source, string destination,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var srcRoot = Path.GetPathRoot(Path.GetFullPath(source));
            var dstRoot = Path.GetPathRoot(Path.GetFullPath(destination));

            // Same drive: instant rename.
            if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(source, destination);
                return;
            }

            // Different drives: copy with progress, then delete the source.
            await Task.Run(() =>
            {
                long total = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                long copied = 0;
                var clock = Stopwatch.StartNew();

                CopyDirectory(source, destination, ct, bytes =>
                {
                    copied += bytes;
                    if (clock.ElapsedMilliseconds >= 200)
                    {
                        Report(progress, gameId, "Moving files", total > 0 ? (double)copied / total : 0);
                        clock.Restart();
                    }
                });
            }, ct).ConfigureAwait(false);

            DeleteDirectory(source);
        }

        private static void CopyDirectory(string source, string destination, CancellationToken ct, Action<long> onBytes)
        {
            Directory.CreateDirectory(destination);
            var buffer = new byte[BufferSize];

            foreach (var file in Directory.GetFiles(source))
            {
                ct.ThrowIfCancellationRequested();
                var destFile = Path.Combine(destination, Path.GetFileName(file));

                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
                using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    onBytes(read);
                }
            }

            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), ct, onBytes);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// FreeArc direct-extraction fallback used when the Inno Setup installer crashes with
        /// 0xE06D7363 (typically caused by old UI-skin DLLs like botva2.dll on modern Windows).
        /// <para>
        /// Strategy:
        ///   1. Use bundled innoextract.exe to unpack the helper tools (unarc.exe, arc.ini, …)
        ///      embedded inside setup.exe into a temporary working directory.
        ///   2. Use the extracted unarc.exe to directly decompress each Data*.bin file from the
        ///      staging folder into <paramref name="targetPath"/> — completely bypassing setup.tmp
        ///      and its crashing DLLs.
        /// </para>
        /// </summary>
        private async Task<OperationResult> TryFreeArcExtractionAsync(
            string gameId, string setupExe, string stagingDirectory, string targetPath,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            // ── 1. Locate innoextract ─────────────────────────────────────────────────
            var innoextractExe = LocateSideTool("innoextract.exe");
            if (innoextractExe == null)
            {
                _logger.LogWarning("innoextract.exe not found — cannot attempt FreeArc fallback.");
                return OperationResult.Fail("innoextract.exe not found.");
            }

            // ── 2. Extract unarc.exe + helpers from setup.exe ────────────────────────
            var workDir = Path.Combine(Path.GetTempPath(), $"nexora_unarc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);

            try
            {
                _logger.LogInformation("Extracting FreeArc tools from {Setup} to {WorkDir}", setupExe, workDir);
                Report(progress, gameId, "Extracting installer tools", 0.02, null);

                using (var ie = Process.Start(new ProcessStartInfo
                {
                    FileName        = innoextractExe,
                    Arguments       = $"--extract --output-dir \"{workDir}\" \"{setupExe}\"",
                    WorkingDirectory = workDir,
                    UseShellExecute  = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                    CreateNoWindow   = true
                }))
                {
                    if (ie == null)
                        return OperationResult.Fail("Could not start innoextract.");
                    await ie.WaitForExitAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("innoextract exited with code {Code}", ie.ExitCode);
                }

                // innoextract places temp files under <workDir>\tmp\
                var toolsDir = Path.Combine(workDir, "tmp");
                var unarcExe = Path.Combine(toolsDir, "unarc.exe");

                if (!File.Exists(unarcExe))
                {
                    _logger.LogWarning("unarc.exe not found after innoextract at {Path}", unarcExe);
                    return OperationResult.Fail("unarc.exe not found inside setup.exe.");
                }

                // ── 3. Find Data*.bin archives in the staging directory ───────────────
                var binFiles = Directory
                    .EnumerateFiles(stagingDirectory, "*.bin", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                        // Match Data.bin, Data1.bin … Data4.bin, Data-eng.bin, Data-rus.bin, etc.
                        return n == "data" || n.StartsWith("data", StringComparison.Ordinal);
                    })
                    .OrderBy(f => f)   // Data.bin first, then Data1…
                    .ToList();

                if (binFiles.Count == 0)
                {
                    _logger.LogWarning("No Data*.bin archives found in {Staging}", stagingDirectory);
                    return OperationResult.Fail("No Data*.bin archives found.");
                }

                _logger.LogInformation("Found {Count} FreeArc archive(s): {Files}",
                    binFiles.Count, string.Join(", ", binFiles.Select(Path.GetFileName)));

                // ── 4. Extract each archive ───────────────────────────────────────────
                Directory.CreateDirectory(targetPath);

                for (int i = 0; i < binFiles.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var bin = binFiles[i];
                    double frac = 0.05 + 0.90 * i / binFiles.Count;

                    Report(progress, gameId,
                        $"Extracting {Path.GetFileName(bin)} ({i + 1}/{binFiles.Count})",
                        frac, Path.GetFileName(bin));

                    _logger.LogInformation("Extracting {Bin} → {Target}", bin, targetPath);

                    using var unarc = Process.Start(new ProcessStartInfo
                    {
                        FileName         = unarcExe,
                        // x = extract with full paths, -dp = destination path, -o+ = overwrite
                        Arguments        = $"x \"{bin}\" -dp\"{targetPath}\" -o+",
                        WorkingDirectory = toolsDir,   // arc.ini must be in CWD
                        UseShellExecute  = false,
                        CreateNoWindow   = true
                    });

                    if (unarc == null)
                    {
                        _logger.LogWarning("Could not start unarc.exe for {Bin}", bin);
                        return OperationResult.Fail($"Could not start unarc.exe for {Path.GetFileName(bin)}.");
                    }

                    try
                    {
                        await unarc.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!unarc.HasExited) unarc.Kill(true); } catch { }
                        throw;
                    }

                    _logger.LogInformation("unarc exited with code {Code} for {Bin}", unarc.ExitCode, Path.GetFileName(bin));

                    if (unarc.ExitCode != 0)
                        return OperationResult.Fail(
                            $"FreeArc extraction failed for {Path.GetFileName(bin)} (exit code {unarc.ExitCode}).");
                }

                Report(progress, gameId, "Extraction complete", 1.0, null);
                _logger.LogInformation("FreeArc direct extraction completed successfully for {GameId}", gameId);
                return OperationResult.Ok();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FreeArc extraction failed for {GameId}", gameId);
                return OperationResult.Fail($"FreeArc extraction error: {ex.Message}");
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Locates a bundled side-tool exe (e.g. innoextract.exe) next to Nexora.exe,
        /// or in the dev-workspace tools/bin folder.
        /// </summary>
        private static string? LocateSideTool(string fileName)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(AppContext.BaseDirectory, "tools", "bin", fileName),
                PathHelper.FindFileWalkingUp(Path.Combine("tools", "bin", fileName))
            };
            return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        }

        /// <summary>
        /// Silently installs VCRedist packages found in the Redist/ subdirectory of
        /// <paramref name="stagingDirectory"/>. Typical repack layout:
        ///   Redist/vcredist_x86.exe, Redist/vcredist_x64.exe, Redist/DXSETUP.exe, …
        /// Each installer is run with /install /quiet /norestart so it exits quickly when the
        /// runtime is already present.  Errors and non-zero exit codes are only logged — a missing
        /// or failing redist is not fatal here; if setup.exe still crashes we'll surface a clear
        /// message to the user afterward.
        /// </summary>
        private async Task InstallBundledRedistAsync(
            string stagingDirectory, IProgress<DownloadProgress>? progress, string gameId, CancellationToken ct)
        {
            // Look for vcredist_*.exe and vc_redist*.exe in a Redist (or redist) subfolder.
            var redistDir = Directory.EnumerateDirectories(stagingDirectory, "Redist", SearchOption.AllDirectories)
                                     .FirstOrDefault()
                         ?? Directory.EnumerateDirectories(stagingDirectory, "redist", SearchOption.AllDirectories)
                                     .FirstOrDefault();

            if (redistDir == null)
            {
                _logger.LogDebug("No Redist/ folder found under {Staging} — skipping prerequisite install.", stagingDirectory);
                return;
            }

            var candidates = Directory.EnumerateFiles(redistDir, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return name.StartsWith("vcredist", StringComparison.Ordinal)
                        || name.StartsWith("vc_redist", StringComparison.Ordinal);
                })
                .OrderBy(f => f) // x86 before x64 — install 32-bit runtime first
                .ToList();

            if (candidates.Count == 0)
            {
                _logger.LogDebug("Redist/ folder found but contains no vcredist installers.");
                return;
            }

            _logger.LogInformation("Installing {Count} bundled VCRedist package(s) before running main setup.", candidates.Count);
            Report(progress, gameId, "Installing prerequisites", 0, "Visual C++ Redistributable");

            foreach (var redistExe in candidates)
            {
                if (!EnsureInstallerConsent(gameId, redistExe, elevated: true, silent: true, "bundled-prerequisite"))
                {
                    _logger.LogWarning(
                        "Skipping bundled prerequisite {File}: user declined.",
                        Path.GetFileName(redistExe));
                    continue;
                }

                _logger.LogInformation("Running VCRedist: {Path}", redistExe);
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName        = redistExe,
                        Arguments       = "/install /quiet /norestart",
                        WorkingDirectory = redistDir,
                        UseShellExecute = true,
                        Verb            = "runas"   // VCRedist always needs elevation
                    });

                    if (proc == null)
                    {
                        _logger.LogWarning("Could not start VCRedist installer: {Path}", redistExe);
                        continue;
                    }

                    try
                    {
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!proc.HasExited) proc.Kill(true); } catch { }
                        throw;
                    }

                    // 0 = success; 3010 = success, reboot required (common for VCRedist — ignore)
                    if (proc.ExitCode is 0 or 3010)
                        _logger.LogInformation("VCRedist {File} installed (code {Code}).", Path.GetFileName(redistExe), proc.ExitCode);
                    else
                        _logger.LogWarning("VCRedist {File} exited with code {Code} — continuing anyway.", Path.GetFileName(redistExe), proc.ExitCode);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to run VCRedist installer {Path} — continuing anyway.", redistExe);
                }
            }
        }

        /// <summary>
        /// Runs the installer and waits for it to exit.
        /// When <paramref name="elevated"/> is true the "runas" shell verb is used to request
        /// administrator privileges via UAC before the process starts.
        /// Returns the process exit code, or <see cref="int.MinValue"/> if the process could not be started.
        /// </summary>
        private async Task<int> RunElevatedOrShellAsync(
            string exePath, string arguments, string workingDirectory, bool elevated, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                };
                if (elevated)
                    psi.Verb = "runas";

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    _logger.LogWarning("Process.Start returned null for {Exe} (elevated={Elevated})", exePath, elevated);
                    return int.MinValue;
                }

                // Guard against InvalidOperationException ("No process is associated
                // with this object") — same ShellExecute issue as in RunSetupAsync.
                try
                {
                    _ = proc.HasExited; // throws if handle is stale
                }
                catch (InvalidOperationException)
                {
                    _logger.LogWarning("Process handle is stale for {Exe} (elevated={Elevated})", exePath, elevated);
                    return int.MinValue;
                }

                try
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                    throw;
                }

                _logger.LogInformation("Installer {Exe} exited with code {Code} (elevated={Elevated})",
                    exePath, proc.ExitCode, elevated);
                return proc.ExitCode;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start installer {Exe} (elevated={Elevated})", exePath, elevated);
                return int.MinValue;
            }
        }

        /// <summary>Asks the UI before executing an installer from a downloaded package.</summary>
        private static bool EnsureInstallerConsent(
            string gameId, string installerPath, bool elevated, bool silent, string reason)
        {
            long size = 0;
            try { size = new FileInfo(installerPath).Length; }
            catch { /* the file may disappear between discovery and the prompt */ }

            return InstallerConsent.Request(new InstallerRunRequest(
                gameId, gameId, installerPath, size, elevated, silent, reason));
        }

        private static void Report(IProgress<DownloadProgress>? progress, string gameId,
            string phase, double fraction, string? currentFile = null)
        {
            progress?.Report(new DownloadProgress
            {
                GameId = gameId,
                Phase = phase,
                PhaseKey = DownloadPhases.FromLegacyText(phase),
                Fraction = Math.Clamp(fraction, 0, 1),
                CurrentFile = currentFile ?? string.Empty
            });
        }

        /// <summary>Deletes a folder, clearing read-only attributes first (games often have them).</summary>
        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;

            if (!PathHelper.IsSafeToDeleteTree(path))
                throw new InvalidOperationException($"Refusing to delete a suspicious path: {path}");

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(path, true);
        }

        private void CleanupGameStaging(string gameId, string stagingPath)
        {
            if (!PathHelper.DeleteGameStagingDirectory(stagingPath, gameId))
                _logger.LogWarning(
                    "Could not safely remove staging folder for {GameId}; left it untouched: {Path}",
                    gameId, stagingPath);
        }

        private void DeleteDirectorySafe(string path)
        {
            try { DeleteDirectory(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete {Path}", path); }
        }
    }
}
