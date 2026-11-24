using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Vellum.Migrator;

public sealed class MigrationResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
}

public sealed class MigrationService
{
    private readonly MigrationOptions _options;
    private readonly List<string> _messages = new();

    public MigrationService(MigrationOptions options)
    {
        _options = options;
        Detect();
    }

    public string TargetProfile => Path.Combine(_options.RoamingRoot, "Vellum");
    public string? LegacyProfile { get; private set; }
    public string? LegacyExecutable { get; private set; }
    public string? LegacyUninstaller { get; private set; }
    public string? VellumExecutable { get; private set; }
    public string? VellumInstaller { get; private set; }

    public void Detect()
    {
        LegacyProfile = new[] { "Nexora", "GameVault" }
            .Select(name => Path.Combine(_options.RoamingRoot, name))
            .FirstOrDefault(Directory.Exists);

        DetectLegacyInstall();
        VellumExecutable = FindVellumExecutable();
        VellumInstaller = ResolveInstallerOverride() ?? FindInstallerNearby();
    }

    private void DetectLegacyInstall()
    {
        LegacyExecutable = null;
        LegacyUninstaller = null;

        foreach (var directory in EnumerateLegacyInstallDirectories())
        {
            var executable = Path.Combine(directory, "Nexora.exe");
            if (!File.Exists(executable))
                continue;

            LegacyExecutable = executable;
            var uninstaller = Path.Combine(directory, "unins000.exe");
            if (File.Exists(uninstaller))
                LegacyUninstaller = uninstaller;
            break;
        }

        if (LegacyUninstaller is not null)
            return;

        var fromRegistry = FindUninstallerByDisplayName("Nexora");
        if (fromRegistry is null)
            return;

        LegacyUninstaller = fromRegistry;
        var installDirectory = Path.GetDirectoryName(fromRegistry);
        if (installDirectory is not null && LegacyExecutable is null)
        {
            var executable = Path.Combine(installDirectory, "Nexora.exe");
            if (File.Exists(executable))
                LegacyExecutable = executable;
        }
    }

    private IEnumerable<string> EnumerateLegacyInstallDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<string>
        {
            Path.Combine(_options.InstallRoot, "Nexora"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Nexora"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nexora"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Nexora")
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;

            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Nexora"));
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Programs", "Nexora"));
        }

        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    private string? ResolveInstallerOverride()
    {
        var path = _options.VellumInstallerPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        return IsVellumInstaller(path) ? path : null;
    }

    private static bool IsVellumInstaller(string path)
    {
        if (!File.Exists(path))
            return false;

        var name = Path.GetFileName(path);
        if (!name.StartsWith("Vellum-Setup-", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.ProductName)
                && info.ProductName.Contains("Vellum", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(info.FileDescription)
                && info.FileDescription.Contains("Vellum", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // Older setup builds may not carry version info; the file name is authoritative.
        return true;
    }

    private static string? GetInstallerVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (!string.IsNullOrWhiteSpace(version))
                return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the file name below.
        }

        var match = Regex.Match(
            Path.GetFileName(path),
            @"Vellum-Setup-(\d+\.\d+\.\d+)-win-x64\.exe",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public MigrationResult Run(Action<string>? report = null)
    {
        Detect();
        _messages.Clear();
        var success = true;

        try
        {
            Report($"Профиль Nexora: {LegacyProfile ?? "не найден"}", report);
            Report($"Профиль Vellum: {TargetProfile}", report);
            Report($"Старая версия: {DescribeLegacyInstall()}", report);
            Report($"Новая версия: {VellumExecutable ?? VellumInstaller ?? "не найдена"}", report);
            if (VellumInstaller is not null)
            {
                var installerVersion = GetInstallerVersion(VellumInstaller);
                Report($"Установщик Vellum: {VellumInstaller}" +
                       (installerVersion is null ? string.Empty : $" (версия {installerVersion})"), report);
            }
            else if (!string.IsNullOrWhiteSpace(_options.VellumInstallerPath))
            {
                Report($"Указанный установщик не подходит: {_options.VellumInstallerPath}", report);
            }

            if (_options.DryRun)
                Report("Режим проверки: изменения не вносятся.", report);

            if (_options.MigrateProfile)
                success &= MigrateProfile(report);

            if (_options.UninstallOld)
                success &= UninstallLegacy(report);

            if (_options.LaunchVellum)
                success &= LaunchVellum(report);

            Report(success ? "Готово." : "Завершено с ошибками.", report);
        }
        catch (Exception ex)
        {
            success = false;
            Report($"Ошибка: {ex.Message}", report);
        }
        finally
        {
            WriteLog();
        }

        return new MigrationResult { Success = success, Messages = _messages.ToArray() };
    }

    private bool MigrateProfile(Action<string>? report)
    {
        if (LegacyProfile is null)
        {
            Report("Старый профиль Nexora не найден — переносить нечего.", report);
            return true;
        }

        var targetExisted = Directory.Exists(TargetProfile);
        if (_options.DryRun)
        {
            Report($"Будет перенесён профиль: {LegacyProfile} -> {TargetProfile}", report);
            return true;
        }

        var copied = CopyMissingFiles(LegacyProfile, TargetProfile);
        Report($"Профиль: {LegacyProfile} -> {TargetProfile}", report);
        Report($"Скопировано файлов: {copied}.", report);
        if (!targetExisted)
            Report("Создан профиль Vellum.", report);

        return true;
    }

    private bool UninstallLegacy(Action<string>? report)
    {
        if (LegacyUninstaller is null)
        {
            if (LegacyExecutable is not null)
            {
                Report($"Портативная версия Nexora: {LegacyExecutable}", report);
                Report("Автоматическое удаление недоступно — удали папку вручную после проверки.", report);
                return true;
            }

            Report("Установленная Nexora не найдена — удалять нечего.", report);
            return true;
        }

        if (_options.DryRun)
        {
            Report($"Будет запущен деинсталлятор: {LegacyUninstaller}", report);
            return true;
        }

        Report("Удаление старой версии Nexora (может потребоваться подтверждение UAC)...", report);
        var startInfo = new ProcessStartInfo(LegacyUninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(LegacyUninstaller) ?? string.Empty
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Report("Не удалось запустить деинсталлятор.", report);
            return false;
        }

        if (!process.WaitForExit(180_000))
        {
            Report("Деинсталлятор не завершился за 3 минуты.", report);
            return false;
        }

        Report($"Деинсталлятор завершён с кодом {process.ExitCode}.", report);
        return process.ExitCode == 0;
    }

    private bool LaunchVellum(Action<string>? report)
    {
        var target = VellumExecutable ?? VellumInstaller;
        if (target is null)
        {
            Report("Vellum не найден. Установи Vellum-Setup и запусти его вручную.", report);
            return true;
        }

        var installDirectory = VellumExecutable is null && !string.IsNullOrWhiteSpace(_options.InstallDirectory)
            ? NormalizeDirectory(_options.InstallDirectory)
            : null;

        if (_options.DryRun)
        {
            if (installDirectory is not null)
                Report($"Каталог установки: {installDirectory}", report);
            Report($"Будет запущено: {target}", report);
            return true;
        }

        try
        {
            var startInfo = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty
            };

            if (installDirectory is not null)
            {
                startInfo.Arguments = $"/DIR=\"{installDirectory}\"";
                Report($"Каталог установки: {installDirectory}", report);
            }

            Process.Start(startInfo);
            Report($"Запущено: {target}", report);
            return true;
        }
        catch (Exception ex)
        {
            Report($"Не удалось запустить {target}: {ex.Message}", report);
            return false;
        }
    }

    private static string NormalizeDirectory(string path)
    {
        var trimmed = path.Trim().Trim('"');
        return trimmed.Length > 3 ? trimmed.TrimEnd('\\', '/') : trimmed;
    }

    private static int CopyMissingFiles(string source, string target)
    {
        Directory.CreateDirectory(target);
        var copied = 0;

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
                continue;

            File.Copy(file, destination);
            copied++;
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
            copied += CopyMissingFiles(directory, Path.Combine(target, Path.GetFileName(directory)));

        return copied;
    }

    private string DescribeLegacyInstall()
    {
        if (LegacyUninstaller is not null)
            return LegacyUninstaller;
        if (LegacyExecutable is not null)
            return $"{LegacyExecutable} (портативная)";
        return "не установлена";
    }

    private string? FindUninstallerByDisplayName(string displayName, bool exact = false)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in new[]
                     {
                         @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key is null)
                        continue;

                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var entry = key.OpenSubKey(name);
                        var current = entry?.GetValue("DisplayName") as string;
                        if (current is null)
                            continue;

                        var matches = exact
                            ? string.Equals(current, displayName, StringComparison.OrdinalIgnoreCase)
                            : current.StartsWith(displayName, StringComparison.OrdinalIgnoreCase);
                        if (!matches)
                            continue;

                        var uninstall = (entry?.GetValue("UninstallString") as string)?.Trim();
                        var executable = ExtractExecutable(uninstall);
                        if (executable is not null && File.Exists(executable))
                            return executable;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // A locked-down registry hive must not stop the migration.
                }
            }
        }

        return null;
    }

    private static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var separator = command.IndexOf(" /", StringComparison.Ordinal);
        return separator > 0 ? command[..separator] : command;
    }

    private string? FindVellumExecutable()
    {
        var standard = Path.Combine(_options.InstallRoot, "Vellum", "Vellum.exe");
        if (File.Exists(standard))
            return standard;

        var uninstaller = FindUninstallerByDisplayName("Vellum", exact: true);
        var directory = Path.GetDirectoryName(uninstaller);
        if (directory is null)
            return null;

        var executable = Path.Combine(directory, "Vellum.exe");
        return File.Exists(executable) ? executable : null;
    }

    private string? FindInstallerNearby()
    {
        var baseDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
            baseDirectory = AppContext.BaseDirectory;

        var candidates = new List<string> { baseDirectory };
        var parent = Path.GetDirectoryName(baseDirectory);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            candidates.Add(parent);
            candidates.Add(Path.Combine(parent, "installer"));

            var grandParent = Path.GetDirectoryName(parent);
            if (!string.IsNullOrWhiteSpace(grandParent))
                candidates.Add(Path.Combine(grandParent, "installer"));
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        candidates.Add(downloads);
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                candidates.Add(drive.RootDirectory.FullName);
        }

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(directory))
                    continue;

                var found = Directory
                    .EnumerateFiles(directory, "Vellum-Setup-*.exe")
                    .Where(IsVellumInstaller)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (found is not null)
                    return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep looking in other known locations.
            }
        }

        return null;
    }

    private void Report(string message, Action<string>? report)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _messages.Add(line);
        report?.Invoke(line);
    }

    private void WriteLog()
    {
        try
        {
            var directory = Path.GetDirectoryName(_options.LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var builder = new StringBuilder();
            builder.AppendLine($"Vellum Migrator {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Профиль: {TargetProfile}");
            foreach (var message in _messages)
                builder.AppendLine(message);

            File.WriteAllText(_options.LogPath, builder.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never hide the migration result.
        }
    }
}
