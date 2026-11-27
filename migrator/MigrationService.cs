using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string LatestReleaseApi =
        "https://api.github.com/repos/LDProjectTeam/Vellum/releases/latest";

    private const string WebsiteReleases =
        "https://ldprojectteam.github.io/Vellum/releases.json";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex InstallerNamePattern = new(
        @"^Vellum-Setup-(\d+\.\d+\.\d+)-win-x64\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ProfileJsonFiles = { "settings.json", "library.json" };
    private static readonly string[] LegacyBrands = { "Nexora", "GameVault" };

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
    public string? LegacyInstallDirectory { get; private set; }
    public string? LegacyLibraryPath { get; private set; }
    public string? VellumExecutable { get; private set; }
    public string? VellumInstaller { get; private set; }
    public string? VellumInstallDirectory { get; private set; }

    public string? TargetLibraryPath => VellumInstallDirectory is null
        ? null
        : Path.Combine(VellumInstallDirectory, "Library");

    public void Detect()
    {
        LegacyProfile = LegacyBrands
            .Select(name => Path.Combine(_options.RoamingRoot, name))
            .FirstOrDefault(Directory.Exists);

        VellumExecutable = FindVellumExecutable();
        VellumInstallDirectory = ResolveVellumInstallDirectory(VellumExecutable);
        VellumInstaller = ResolveInstallerOverride() ?? FindInstallerNearby();

        DetectLegacyInstall();
        LegacyLibraryPath = ResolveLegacyLibraryPath();
    }

    private void DetectLegacyInstall()
    {
        LegacyExecutable = null;
        LegacyUninstaller = null;
        LegacyInstallDirectory = null;

        foreach (var directory in EnumerateLegacyCandidates())
        {
            if (!Directory.Exists(directory))
                continue;

            if (VellumInstallDirectory is not null
                && IsSameOrInside(directory, VellumInstallDirectory))
                continue;

            if (!LooksLikeInstall(directory))
                continue;

            LegacyInstallDirectory = directory;
            LegacyExecutable = FirstExisting(
                Path.Combine(directory, "Nexora.exe"),
                Path.Combine(directory, "Vellum.exe"));

            var uninstaller = Path.Combine(directory, "unins000.exe");
            if (File.Exists(uninstaller))
                LegacyUninstaller = uninstaller;
            break;
        }

        if (LegacyUninstaller is null)
            LegacyUninstaller = FindUninstallerByDisplayName("Nexora")
                                ?? FindUninstallerByDisplayName("GameVault");

        // An uninstaller in the registry can still point at the legacy folder even
        // when the folder itself was renamed or moved out of the usual locations.
        if (LegacyInstallDirectory is null && LegacyUninstaller is not null)
        {
            var directory = Path.GetDirectoryName(LegacyUninstaller);
            if (!string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && !(VellumInstallDirectory is not null && IsSameOrInside(directory, VellumInstallDirectory)))
            {
                LegacyInstallDirectory = directory;
                LegacyExecutable = FirstExisting(
                    Path.Combine(directory, "Nexora.exe"),
                    Path.Combine(directory, "Vellum.exe"));
            }
        }
    }

    private IEnumerable<string> EnumerateLegacyCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        foreach (var brand in LegacyBrands)
        {
            candidates.Add(Path.Combine(_options.InstallRoot, brand));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), brand));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), brand));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), brand));

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;

                candidates.Add(Path.Combine(drive.RootDirectory.FullName, brand));
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Programs", brand));
            }
        }

        candidates.AddRange(FindLegacyDirectoriesInRegistry());

        // A renamed legacy install can also be inferred from the library path that
        // the profile still points at (for example D:\Nexora\Library).
        var library = ReadProfileInstallPath(TargetProfile)
                      ?? ReadProfileInstallPath(LegacyProfile);
        if (!string.IsNullOrWhiteSpace(library))
        {
            var parent = Path.GetDirectoryName(library);
            if (!string.IsNullOrWhiteSpace(parent))
                candidates.Add(parent);
        }

        foreach (var candidate in candidates)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(full))
                yield return full;
        }
    }

    private static bool LooksLikeInstall(string directory)
    {
        if (File.Exists(Path.Combine(directory, "Nexora.exe")))
            return true;

        // Candidates come from folders named Nexora/GameVault, so a library is a
        // strong enough marker even after a partial uninstall removed the app files.
        if (Directory.Exists(Path.Combine(directory, "Library")))
            return true;

        if (File.Exists(Path.Combine(directory, "Vellum.exe"))
            || File.Exists(Path.Combine(directory, "unins000.exe")))
        {
            return Directory.Exists(Path.Combine(directory, "data"))
                   || Directory.Exists(Path.Combine(directory, "tools"));
        }

        return false;
    }

    private static string? FirstExisting(params string[] paths)
        => paths.FirstOrDefault(File.Exists);

    private static string? ReadProfileInstallPath(string? profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
            return null;

        var settings = Path.Combine(profileDirectory, "settings.json");
        if (!File.Exists(settings))
            return null;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(settings)) is not JsonObject root)
                return null;

            var value = root["DefaultInstallPath"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? ResolveLegacyLibraryPath()
    {
        if (LegacyInstallDirectory is null)
            return null;

        // The legacy profile has priority: it is the profile that belonged to the
        // old installation. After a first Vellum start the app may already have
        // moved the profile, so the current profile is the next best source.
        foreach (var profile in new[] { LegacyProfile, TargetProfile })
        {
            var path = ReadProfileInstallPath(profile);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                continue;

            if (VellumInstallDirectory is not null && IsSameOrInside(path, VellumInstallDirectory))
                continue;

            if (IsSameOrInside(path, LegacyInstallDirectory) || Directory.Exists(Path.Combine(path, "temp")))
                return Path.GetFullPath(path);
        }

        var conventional = Path.Combine(LegacyInstallDirectory, "Library");
        return Directory.Exists(conventional) ? conventional : null;
    }

    private string? ResolveVellumInstallDirectory(string? executable)
    {
        if (executable is not null)
            return Path.GetDirectoryName(executable);

        if (!string.IsNullOrWhiteSpace(_options.InstallDirectory))
            return NormalizeDirectory(_options.InstallDirectory);

        return Path.Combine(_options.InstallRoot, "Vellum");
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

        var match = InstallerNamePattern.Match(Path.GetFileName(path));
        return match.Success ? match.Groups[1].Value : null;
    }

    public MigrationResult Run(Action<string>? report = null)
    {
        Detect();
        _messages.Clear();
        var success = true;

        try
        {
            Report($"Профиль Nexora: {LegacyProfile ?? "не найден (возможно, уже перенесён)"}", report);
            Report($"Профиль Vellum: {TargetProfile}", report);
            Report($"Старая папка: {LegacyInstallDirectory ?? "не найдена"}", report);
            Report($"Папка Vellum: {VellumInstallDirectory ?? "не найдена"}", report);
            Report($"Библиотека: {LegacyLibraryPath ?? "не найдена"} -> {TargetLibraryPath ?? "не найдена"}", report);
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

            if (!_options.DryRun && IsProcessRunning("Vellum"))
            {
                Report("Vellum сейчас запущен. Закрой его, иначе настройки и файлы" +
                       " библиотеки могут остаться занятыми.", report);
            }

            if (_options.MigrateProfile)
                success &= MigrateProfile(report);

            var inPlace = LegacyInstallDirectory is not null
                          && VellumInstallDirectory is not null
                          && IsSamePath(LegacyInstallDirectory, VellumInstallDirectory);

            string? snapshot = null;
            var uninstallHandled = false;
            var inPlaceHandled = false;
            var inPlaceTarget = inPlace ? VellumInstallDirectory : null;

            // When Vellum is installed over the legacy folder, preserve data and
            // tools before the old uninstaller removes them, then install, then
            // restore them on top of the fresh installation.
            if (inPlace && _options.UninstallOld)
            {
                if (_options.MigrateInstallData)
                    snapshot = SnapshotLegacyUserData(report);

                success &= UninstallLegacy(report);
                uninstallHandled = true;
                Detect();

                if (snapshot is not null && inPlaceTarget is not null)
                {
                    inPlaceHandled = RestoreInPlace(snapshot, inPlaceTarget, report);
                    success &= inPlaceHandled;
                }
            }

            var installDataOk = true;
            if (_options.MigrateInstallData && !inPlaceHandled)
                installDataOk = MigrateInstallData(report);

            if (!installDataOk)
                success = false;

            if (_options.UninstallOld && !uninstallHandled)
            {
                if (!installDataOk && LegacyLibraryStillInsideInstall())
                {
                    Report("Удаление старой версии пропущено: сначала нужно перенести" +
                           " данные, иначе игры будут потеряны.", report);
                    success = false;
                }
                else
                {
                    success &= UninstallLegacy(report);
                }
            }

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
            Report("Старый профиль Nexora не найден — профиль уже перенесён или отсутствует.", report);
            return true;
        }

        if (IsSamePath(LegacyProfile, TargetProfile))
        {
            Report("Профили уже совпадают — копировать нечего.", report);
            return true;
        }

        var targetExisted = Directory.Exists(TargetProfile);
        if (_options.DryRun)
        {
            Report($"Будет перенесён профиль: {LegacyProfile} -> {TargetProfile}", report);
            return true;
        }

        var stats = new CopyStats();
        var errors = new List<string>();
        var excluded = new HashSet<string>(ProfileJsonFiles, StringComparer.OrdinalIgnoreCase);

        CopyDirectory(LegacyProfile, TargetProfile, overwrite: true, excluded, stats, errors);
        var settingsMerged = MergeProfileJson(LegacyProfile, TargetProfile, "settings.json",
            mergeLibrary: false, report);
        var libraryMerged = MergeProfileJson(LegacyProfile, TargetProfile, "library.json",
            mergeLibrary: true, report);

        Report($"Профиль: {LegacyProfile} -> {TargetProfile}", report);
        Report($"Файлов скопировано: {stats.Copied}, обновлено: {stats.Updated}," +
               $" пропущено: {stats.Skipped}, ошибок: {stats.Failed}.", report);
        foreach (var error in errors.Take(10))
            Report($"Не удалось скопировать: {error}", report);

        if (!targetExisted)
            Report("Создан профиль Vellum.", report);

        if (stats.Failed == 0 && settingsMerged && libraryMerged)
            RenameLegacyProfileAsBackup(report);

        return stats.Failed == 0 && settingsMerged && libraryMerged;
    }

    private void RenameLegacyProfileAsBackup(Action<string>? report)
    {
        if (LegacyProfile is null)
            return;

        var backup = LegacyProfile + ".backup";
        var index = 1;
        while (Directory.Exists(backup) || File.Exists(backup))
            backup = LegacyProfile + ".backup" + index++;

        try
        {
            // Vellum merges a folder called Nexora/GameVault on startup. Leaving the
            // already-copied profile in place would create *.previousN duplicates, so
            // the old profile is renamed out of the way and kept as a real backup.
            Directory.Move(LegacyProfile, backup);
            Report($"Старый профиль сохранён как резервная копия: {backup}", report);
            LegacyProfile = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Не удалось переименовать старый профиль в резервную копию: {ex.Message}", report);
        }
    }

    private bool MergeProfileJson(
        string legacyProfile,
        string targetProfile,
        string fileName,
        bool mergeLibrary,
        Action<string>? report)
    {
        var sourcePath = Path.Combine(legacyProfile, fileName);
        var targetPath = Path.Combine(targetProfile, fileName);
        if (!File.Exists(sourcePath))
            return true;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(sourcePath)) is not { } sourceRoot)
                return true;

            JsonNode result;
            if (mergeLibrary)
            {
                // library.json is normally an array; accept a lone object too, so a
                // hand-edited or PowerShell-written file is not silently dropped.
                JsonArray? sourceArray = sourceRoot switch
                {
                    JsonArray array => array.DeepClone() as JsonArray,
                    JsonObject single => new JsonArray(single.DeepClone()),
                    _ => null
                };

                if (sourceArray is null)
                    return false;

                var merged = new JsonArray();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in sourceArray)
                {
                    if (item is not JsonObject entry || !seen.Add(GetLibraryKey(entry)))
                        continue;
                    merged.Add(entry.DeepClone());
                }

                if (File.Exists(targetPath)
                    && JsonNode.Parse(File.ReadAllText(targetPath)) is JsonArray targetArray)
                {
                    foreach (var item in targetArray)
                    {
                        if (item is not JsonObject entry || !seen.Add(GetLibraryKey(entry)))
                            continue;
                        merged.Add(entry.DeepClone());
                    }
                }

                result = merged;
            }
            else if (!mergeLibrary && sourceRoot is JsonObject sourceObject)
            {
                var merged = new JsonObject();
                if (File.Exists(targetPath)
                    && JsonNode.Parse(File.ReadAllText(targetPath)) is JsonObject targetObject)
                {
                    foreach (var pair in targetObject)
                        merged[pair.Key] = pair.Value?.DeepClone();
                }

                // The legacy profile is the user's real configuration, so its values
                // win over defaults that Vellum may have written on first launch.
                foreach (var pair in sourceObject)
                    merged[pair.Key] = pair.Value?.DeepClone();

                result = merged;
            }
            else
            {
                return false;
            }

            SaveJson(targetPath, result);
            Report($"Объединён {fileName}: {sourcePath} -> {targetPath}", report);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException or InvalidOperationException)
        {
            Report($"Не удалось объединить {fileName}: {ex.Message}", report);
            return false;
        }
    }

    private static string GetLibraryKey(JsonObject entry)
    {
        var id = entry["GameId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(id))
            return "id:" + id;

        var name = entry["DisplayName"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(name))
            return "name:" + name;

        return "path:" + entry["InstallPath"]?.GetValue<string>();
    }

    private bool MigrateInstallData(Action<string>? report)
    {
        Detect();

        if (LegacyInstallDirectory is null)
        {
            Report("Старая папка Nexora не найдена — Library, data и tools переносить не из чего.", report);
            return true;
        }

        if (VellumInstallDirectory is null)
        {
            Report("Не удалось определить папку Vellum.", report);
            return false;
        }

        var inPlace = IsSamePath(LegacyInstallDirectory, VellumInstallDirectory);

        if (_options.DryRun)
        {
            if (inPlace)
                Report("Старая и новая папки совпадают — данные останутся на месте.", report);
            else
            {
                Report($"Будет перенесена папка: {LegacyInstallDirectory} -> {VellumInstallDirectory}", report);
                if (LegacyLibraryPath is not null)
                    Report($"Библиотека: {LegacyLibraryPath} -> {Path.Combine(VellumInstallDirectory, "Library")}", report);
            }
            Report("Папки data и tools будут объединены с установленным Vellum.", report);
            return true;
        }

        if (inPlace)
        {
            if (VellumExecutable is null)
            {
                // Installing over the legacy folder overwrites data/games.json, so
                // keep the user's data and tools and put them back afterwards.
                var backup = SnapshotLegacyUserData(report);
                if (!EnsureVellumInstalled(report))
                {
                    Report("Vellum не установлен в старую папку.", report);
                    return false;
                }

                Detect();
                if (backup is not null)
                    RestoreSnapshot(backup, VellumInstallDirectory ?? LegacyInstallDirectory, report);
            }

            Report("Старая и новая папки совпадают — Library, data и tools уже на месте.", report);
            return true;
        }

        if (VellumExecutable is null)
        {
            if (!EnsureVellumInstalled(report))
            {
                Report("Vellum не установлен, но данные можно подготовить: создаю папку Vellum.", report);
            }
            else
            {
                Detect();
            }
        }

        if (!Directory.Exists(VellumInstallDirectory))
        {
            try
            {
                Directory.CreateDirectory(VellumInstallDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"Не удалось создать {VellumInstallDirectory}: {ex.Message}", report);
                return false;
            }
        }

        var success = true;

        if (_options.TransferLibrary && !string.IsNullOrWhiteSpace(LegacyLibraryPath)
            && Directory.Exists(LegacyLibraryPath))
        {
            var newLibrary = Path.Combine(VellumInstallDirectory, "Library");
            var moved = TransferLibrary(LegacyLibraryPath!, newLibrary, report);
            success &= moved;
            if (moved)
                success &= UpdateLibraryPaths(LegacyLibraryPath!, newLibrary, report);
        }
        else if (_options.TransferLibrary)
        {
            Report("Папка Library в старой версии не найдена — пропускаю.", report);
        }

        success &= MergeFolder(
            Path.Combine(LegacyInstallDirectory, "data"),
            Path.Combine(VellumInstallDirectory, "data"),
            "data", report);

        success &= MergeFolder(
            Path.Combine(LegacyInstallDirectory, "tools"),
            Path.Combine(VellumInstallDirectory, "tools"),
            "tools", report);

        return success;
    }

    private bool TransferLibrary(string source, string target, Action<string>? report)
    {
        if (IsSamePath(source, target))
        {
            Report("Библиотека уже находится в папке Vellum.", report);
            return true;
        }

        long size = 0;
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                count++;
                try { size += new FileInfo(file).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Counting is only for the report.
        }

        Report($"{( _options.MoveLibrary ? "Перенос" : "Копирование")} библиотеки:" +
               $" {source} -> {target} ({count} файлов, {FormatBytes(size)})...", report);

        var moved = _options.MoveLibrary && Directory.Exists(source) && !Directory.Exists(target);
        if (moved)
        {
            try
            {
                Directory.Move(source, target);
                Report("Библиотека перенесена.", report);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"Не удалось переместить библиотеку ({ex.Message}), копирую.", report);
            }
        }

        var stats = new CopyStats();
        var errors = new List<string>();

        if (!Directory.Exists(target))
        {
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"Не удалось создать папку библиотеки {target}: {ex.Message}", report);
                return false;
            }
        }

        if (Directory.Exists(source))
            MergeDirectory(source, target, overwrite: false, stats, errors, report);

        Report($"Библиотека: скопировано {stats.Copied}, пропущено {stats.Skipped}, ошибок {stats.Failed}.", report);
        foreach (var error in errors.Take(10))
            Report($"Не удалось скопировать: {error}", report);

        var copiedAnything = stats.Copied + stats.Updated > 0;
        var sourceIsEmpty = false;
        try { sourceIsEmpty = !Directory.EnumerateFileSystemEntries(source).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (_options.MoveLibrary && stats.Failed == 0 && stats.Skipped == 0
            && (copiedAnything || sourceIsEmpty))
        {
            try
            {
                Directory.Delete(source, recursive: true);
                Report("Старая папка библиотеки удалена после успешного переноса.", report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"Не удалось удалить старую папку библиотеки: {ex.Message}", report);
            }
        }
        else if (_options.MoveLibrary && stats.Skipped > 0)
        {
            Report($"В старой папке остались совпадающие файлы ({stats.Skipped}) —" +
                   " она сохранена как резервная копия.", report);
        }

        return stats.Failed == 0;
    }

    private bool MergeFolder(string source, string target, string label, Action<string>? report)
    {
        if (!Directory.Exists(source))
        {
            Report($"Папка {label} в старой версии не найдена — пропускаю.", report);
            return true;
        }

        if (IsSamePath(source, target))
        {
            Report($"Папка {label} уже находится в Vellum.", report);
            return true;
        }

        var stats = new CopyStats();
        var errors = new List<string>();
        MergeDirectory(source, target, overwrite: true, stats, errors, report);
        Report($"{label}: скопировано {stats.Copied}, обновлено {stats.Updated}," +
               $" ошибок {stats.Failed}.", report);
        foreach (var error in errors.Take(10))
            Report($"Не удалось скопировать: {error}", report);

        return stats.Failed == 0;
    }

    private string? SnapshotLegacyUserData(Action<string>? report)
        => LegacyInstallDirectory is null ? null : SnapshotFolders(LegacyInstallDirectory, report);

    private string? SnapshotFolders(string directory, Action<string>? report)
    {
        var sources = new[] { "data", "tools" }
            .Select(name => (Name: name, Path: Path.Combine(directory, name)))
            .Where(item => Directory.Exists(item.Path))
            .ToArray();

        if (sources.Length == 0)
            return null;

        var backup = Path.Combine(
            Path.GetTempPath(),
            "Vellum-Migrator",
            $"backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(backup);
            foreach (var (name, path) in sources)
            {
                var stats = new CopyStats();
                var errors = new List<string>();
                CopyDirectory(path, Path.Combine(backup, name), overwrite: true,
                    excluded: null, stats, errors);
            }

            Report($"Сохранена резервная копия data/tools: {backup}", report);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Не удалось создать резервную копию data/tools: {ex.Message}", report);
            return null;
        }
    }

    private bool RestoreInPlace(string snapshot, string targetDirectory, Action<string>? report)
    {
        if (!EnsureVellumInstalled(report))
        {
            Report("Не удалось установить Vellum в старую папку — резервная копия" +
                   $" сохранена в {snapshot}.", report);
            return false;
        }

        Detect();
        var restoreTarget = Directory.Exists(targetDirectory)
            ? targetDirectory
            : VellumInstallDirectory ?? targetDirectory;

        Report("Установка поверх старой папки: восстанавливаю data и tools...", report);
        RestoreSnapshot(snapshot, restoreTarget, report);
        return true;
    }

    private void RestoreSnapshot(string snapshot, string targetDirectory, Action<string>? report)
    {
        foreach (var name in new[] { "data", "tools" })
        {
            var source = Path.Combine(snapshot, name);
            if (!Directory.Exists(source))
                continue;

            var stats = new CopyStats();
            var errors = new List<string>();
            MergeDirectory(source, Path.Combine(targetDirectory, name), overwrite: true, stats, errors, report);
            Report($"Восстановлено {name}: {stats.Copied} новых, {stats.Updated} обновлено.", report);
        }

        try { Directory.Delete(snapshot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private bool UninstallLegacy(Action<string>? report)
    {
        if (LegacyUninstaller is null)
        {
            if (LegacyExecutable is not null || LegacyInstallDirectory is not null)
            {
                Report($"Портативная версия Nexora: {LegacyExecutable ?? LegacyInstallDirectory}", report);
                Report("Автоматическое удаление недоступно — удали старую папку вручную после проверки.", report);
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
        if (!EnsureVellumInstalled(report))
            return false;

        Detect();
        if (VellumExecutable is null)
        {
            Report("Vellum не найден после установки. Установи Vellum-Setup вручную.", report);
            return false;
        }

        if (_options.DryRun)
        {
            Report($"Будет запущено: {VellumExecutable}", report);
            return true;
        }

        try
        {
            var bootstrapper = Path.Combine(
                Path.GetDirectoryName(VellumExecutable) ?? string.Empty,
                "Bootstrapper",
                "Vellum.Bootstrapper.exe");
            var launchTarget = File.Exists(bootstrapper) ? bootstrapper : VellumExecutable;
            var startInfo = new ProcessStartInfo(launchTarget)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(VellumExecutable) ?? string.Empty
            };

            Process.Start(startInfo);
            Report($"Запущено: {VellumExecutable}", report);
            return true;
        }
        catch (Exception ex)
        {
            Report($"Не удалось запустить {VellumExecutable}: {ex.Message}", report);
            return false;
        }
    }

    private bool EnsureVellumInstalled(Action<string>? report)
    {
        // The cached executable can be stale after an in-place uninstall.
        Detect();

        var installedVersion = GetExecutableVersion(VellumExecutable);
        var installerVersion = GetInstallerVersionNullable(VellumInstaller);

        if (VellumExecutable is not null)
        {
            var installerIsNewer = VellumInstaller is not null
                                   && IsVersionNewer(installerVersion, installedVersion);
            if (!installerIsNewer)
                return true;

            Report($"Установлена версия Vellum {installedVersion}, обновляю до {installerVersion}.", report);
        }

        var targetDirectory = VellumInstallDirectory;

        if (_options.DryRun)
        {
            Report(VellumInstaller is not null
                ? $"Будет установлен Vellum из {VellumInstaller}"
                : "Установщик Vellum не найден — будет скачана последняя версия.", report);
            return true;
        }

        if (VellumInstaller is null && _options.DownloadLatest)
        {
            var downloaded = DownloadLatestInstallerAsync(report).GetAwaiter().GetResult();
            if (downloaded is not null)
                Detect();
        }

        if (VellumInstaller is null)
        {
            Report("Установщик Vellum не найден. Скачай его вручную и запусти мигратор снова.", report);
            return false;
        }

        // Setup overwrites data/games.json with the shipped default. Keep the
        // user's catalog and engine profile around every installation, not only
        // when upgrading in place.
        string? backup = null;
        if (!string.IsNullOrWhiteSpace(targetDirectory) && Directory.Exists(targetDirectory))
            backup = SnapshotFolders(targetDirectory, report);

        Report($"Установка Vellum из {VellumInstaller}...", report);
        var arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        if (!string.IsNullOrWhiteSpace(targetDirectory))
            arguments += $" /DIR=\"{NormalizeDirectory(targetDirectory)}\"";

        try
        {
            var startInfo = new ProcessStartInfo(VellumInstaller, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(VellumInstaller) ?? string.Empty
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Report("Не удалось запустить установщик Vellum.", report);
                return false;
            }

            if (!process.WaitForExit(600_000))
            {
                Report("Установщик Vellum не завершился за 10 минут.", report);
                return false;
            }

            Report($"Установщик завершён с кодом {process.ExitCode}.", report);
            Detect();

            if (VellumExecutable is null && !string.IsNullOrWhiteSpace(targetDirectory))
            {
                var expected = Path.Combine(NormalizeDirectory(targetDirectory), "Vellum.exe");
                if (File.Exists(expected))
                {
                    VellumExecutable = expected;
                    VellumInstallDirectory = Path.GetDirectoryName(expected);
                }
            }

            if (backup is not null)
            {
                var restoreTarget = VellumInstallDirectory
                                    ?? NormalizeDirectory(targetDirectory!);
                RestoreSnapshot(backup, restoreTarget, report);
            }

            return VellumExecutable is not null;
        }
        catch (Exception ex)
        {
            Report($"Не удалось установить Vellum: {ex.Message}", report);
            return false;
        }
    }

    private static string? GetExecutableVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetInstallerVersionNullable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var version = GetInstallerVersion(path);
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static bool IsVersionNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        if (string.IsNullOrWhiteSpace(current))
            return true;

        if (!Version.TryParse(candidate, out var candidateVersion))
            return false;
        if (!Version.TryParse(current, out var currentVersion))
            return false;

        return candidateVersion > currentVersion;
    }

    /// <summary>
    /// Resolves and downloads the latest Vellum setup. GitHub Releases is the
    /// primary source; the static website manifest is used as a fallback.
    /// </summary>
    public async Task<string?> DownloadLatestInstallerAsync(
        Action<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        Report("Поиск последней версии Vellum...", report);

        var release = await FindLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            Report("Не удалось получить информацию о последней версии Vellum.", report);
            return null;
        }

        Report($"Последняя версия: {release.Version ?? "неизвестна"} ({release.FileName}).", report);

        var directory = Path.Combine(Path.GetTempPath(), "Vellum-Migrator", "downloads");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.FileName);

        if (File.Exists(path)
            && (release.Size <= 0 || new FileInfo(path).Length == release.Size)
            && IsVellumInstaller(path))
        {
            _options.VellumInstallerPath = path;
            Detect();
            Report($"Установщик уже скачан: {path}", report);
            return path;
        }

        var temporary = path + ".part";
        try
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The download below will fail with a clearer error if the file is locked.
        }

        Report($"Скачивание {release.FileName}...", report);
        try
        {
            using var response = await Http.GetAsync(
                release.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.Size;
            var lastPercent = -1;

            await using (var input = await response.Content
                             .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                             temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;

                    if (total > 0)
                    {
                        var percent = (int)(done * 100 / total);
                        if (percent >= lastPercent + 5 || percent == 100)
                        {
                            lastPercent = percent;
                            Report($"Скачано {percent}% ({FormatBytes(done)} из {FormatBytes(total)}).", report);
                        }
                    }
                }
            }

            if (!IsVellumInstaller(temporary))
            {
                TryDelete(temporary);
                Report("Скачанный файл не похож на установщик Vellum.", report);
                return null;
            }

            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporary, path);

            _options.VellumInstallerPath = path;
            Detect();
            Report($"Скачано: {path}", report);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            TryDelete(temporary);
            Report($"Ошибка скачивания: {ex.Message}", report);
            return null;
        }
    }

    private static async Task<ReleaseInfo?> FindLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                var fromGitHub = ParseGitHubRelease(json);
                if (fromGitHub is not null)
                    return fromGitHub;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       or JsonException or TaskCanceledException)
        {
            // Fall through to the website manifest.
        }

        try
        {
            using var response = await Http.GetAsync(WebsiteReleases, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseWebsiteManifest(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       or JsonException or TaskCanceledException
                                       or UriFormatException)
        {
            return null;
        }
    }

    private static ReleaseInfo? ParseGitHubRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            return null;

        ReleaseInfo? best = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                continue;

            var match = InstallerNamePattern.Match(name);
            if (!match.Success)
                continue;

            var version = match.Groups[1].Value;
            var size = asset.TryGetProperty("size", out var sizeElement)
                ? sizeElement.GetInt64()
                : 0;

            if (best is null || IsNewerVersion(version, best.Version))
                best = new ReleaseInfo(url, name, size, version);
        }

        return best;
    }

    private static ReleaseInfo? ParseWebsiteManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("current", out var current))
            return null;

        string? url = null;
        string? fileName = null;
        long size = 0;

        if (current.TryGetProperty("downloads", out var downloads)
            && downloads.TryGetProperty("installer", out var installer))
        {
            if (installer.TryGetProperty("url", out var urlElement))
                url = urlElement.GetString();
            if (installer.TryGetProperty("filename", out var nameElement))
                fileName = nameElement.GetString();
            if (installer.TryGetProperty("sizeBytes", out var sizeElement))
                size = sizeElement.GetInt64();
        }

        if (string.IsNullOrWhiteSpace(url)
            && current.TryGetProperty("installer", out var directUrl))
        {
            url = directUrl.GetString();
        }

        if (string.IsNullOrWhiteSpace(fileName)
            && current.TryGetProperty("filename", out var directName))
        {
            fileName = directName.GetString();
        }

        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (Uri.TryCreate(url, UriKind.Relative, out _))
            url = new Uri(new Uri(WebsiteReleases), url).ToString();

        if (!url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        fileName = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(new Uri(url).LocalPath)
            : fileName;

        var match = InstallerNamePattern.Match(fileName);
        var version = match.Success
            ? match.Groups[1].Value
            : current.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;

        return new ReleaseInfo(url, fileName, size, version);
    }

    private static bool IsNewerVersion(string? candidate, string? current)
    {
        if (Version.TryParse(candidate, out var candidateVersion)
            && Version.TryParse(current, out var currentVersion))
            return candidateVersion > currentVersion;

        return string.Compare(candidate, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private string DescribeLegacyInstall()
    {
        if (LegacyInstallDirectory is not null && LegacyExecutable is not null)
            return $"{LegacyExecutable} (портативная)";
        if (LegacyInstallDirectory is not null)
            return LegacyInstallDirectory;
        if (LegacyUninstaller is not null)
            return LegacyUninstaller;
        return "не установлена";
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private bool LegacyLibraryStillInsideInstall()
    {
        if (LegacyInstallDirectory is null)
            return false;

        foreach (var path in new[] { LegacyLibraryPath, Path.Combine(LegacyInstallDirectory, "Library") })
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                continue;
            if (IsSameOrInside(path, LegacyInstallDirectory))
                return true;
        }

        return false;
    }

    private string? FindUninstallerByDisplayName(string displayName, bool exact = false)
    {
        foreach (var entry in EnumerateUninstallEntries())
        {
            var current = entry.DisplayName;
            if (current is null)
                continue;

            var matches = exact
                ? string.Equals(current, displayName, StringComparison.OrdinalIgnoreCase)
                : current.StartsWith(displayName, StringComparison.OrdinalIgnoreCase);
            if (!matches)
                continue;

            var executable = ExtractExecutable(entry.UninstallString);
            if (executable is not null && File.Exists(executable))
                return executable;
        }

        return null;
    }

    private IEnumerable<string> FindLegacyDirectoriesInRegistry()
    {
        var result = new List<string>();

        foreach (var entry in EnumerateUninstallEntries())
        {
            var displayName = entry.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            var isLegacyBrand = LegacyBrands.Any(brand =>
                displayName.StartsWith(brand, StringComparison.OrdinalIgnoreCase));
            if (!isLegacyBrand && !string.Equals(displayName, "Vellum", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(entry.InstallLocation)
                && Directory.Exists(entry.InstallLocation))
            {
                var directory = NormalizeDirectory(entry.InstallLocation);
                if (isLegacyBrand || IsLegacyFolderName(directory))
                    result.Add(directory);
            }

            var executable = ExtractExecutable(entry.UninstallString);
            var parent = Path.GetDirectoryName(executable);
            if (!string.IsNullOrWhiteSpace(executable)
                && !string.IsNullOrWhiteSpace(parent)
                && Directory.Exists(parent)
                && (isLegacyBrand || IsLegacyFolderName(parent)))
            {
                result.Add(parent);
            }
        }

        return result;
    }

    private static bool IsLegacyFolderName(string directory)
    {
        var name = Path.GetFileName(
            directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return LegacyBrands.Any(brand =>
            string.Equals(name, brand, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<UninstallEntry> EnumerateUninstallEntries()
    {
        var entries = new List<UninstallEntry>();

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
                        if (entry is null)
                            continue;

                        entries.Add(new UninstallEntry(
                            entry.GetValue("DisplayName") as string,
                            entry.GetValue("InstallLocation") as string,
                            (entry.GetValue("UninstallString") as string)?.Trim()));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                               or System.Security.SecurityException)
                {
                    // A locked-down registry hive must not stop the migration.
                }
            }
        }

        return entries;
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
        if (!string.IsNullOrWhiteSpace(_options.InstallDirectory))
        {
            var selected = Path.Combine(NormalizeDirectory(_options.InstallDirectory), "Vellum.exe");
            if (File.Exists(selected))
                return selected;
        }

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
        candidates.Add(Path.Combine(Path.GetTempPath(), "Vellum-Migrator", "downloads"));

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

    private static string NormalizeDirectory(string path)
    {
        var trimmed = path.Trim().Trim('"');
        return trimmed.Length > 3 ? trimmed.TrimEnd('\\', '/') : trimmed;
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

    private bool UpdateLibraryPaths(string oldRoot, string newRoot, Action<string>? report)
    {
        var updated = true;
        var settingsPath = Path.Combine(TargetProfile, "settings.json");
        if (!File.Exists(settingsPath))
        {
            // A first Vellum start without a migrated profile would otherwise show
            // the setup wizard and pick an empty default library instead of the
            // folder that was just moved.
            try
            {
                var settings = new JsonObject { ["DefaultInstallPath"] = newRoot };
                SaveJson(settingsPath, settings);
                Report($"Создан settings.json с библиотекой: {newRoot}", report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                updated = false;
                Report($"Не удалось создать settings.json: {ex.Message}", report);
            }
        }
        else
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings)
                {
                    var current = settings["DefaultInstallPath"]?.GetValue<string>();
                    settings["DefaultInstallPath"] = string.IsNullOrWhiteSpace(current)
                        ? newRoot
                        : RewritePrefix(current, oldRoot, newRoot);
                    SaveJson(settingsPath, settings);
                }
                else
                {
                    updated = false;
                    Report("Не удалось разобрать settings.json.", report);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or JsonException or InvalidOperationException)
            {
                updated = false;
                Report($"Не удалось обновить settings.json: {ex.Message}", report);
            }
        }

        var libraryPath = Path.Combine(TargetProfile, "library.json");
        if (!File.Exists(libraryPath))
            return updated;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(libraryPath)) is not JsonArray library)
            {
                Report("Не удалось разобрать library.json — пути не обновлены.", report);
                return false;
            }

            var changed = 0;
            foreach (var item in library)
            {
                if (item is not JsonObject entry)
                    continue;

                foreach (var property in new[]
                         {
                             "InstallPath", "TargetLibraryPath",
                             "ExternalExecutablePath", "ExternalWorkingDirectory"
                         })
                {
                    var value = entry[property]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var rewritten = RewritePrefix(value, oldRoot, newRoot);
                    if (!string.Equals(rewritten, value, StringComparison.Ordinal))
                    {
                        entry[property] = rewritten;
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                SaveJson(libraryPath, library);
                Report($"Обновлено путей в library.json: {changed}.", report);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException or InvalidOperationException)
        {
            updated = false;
            Report($"Не удалось обновить library.json: {ex.Message}", report);
        }

        return updated;
    }

    private static string RewritePrefix(string value, string oldRoot, string newRoot)
    {
        var oldPrefix = oldRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var newPrefix = newRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                oldPrefix, StringComparison.OrdinalIgnoreCase))
            return newPrefix;

        var withSeparator = oldPrefix + Path.DirectorySeparatorChar;
        if (value.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase))
            return newPrefix + value[oldPrefix.Length..];

        return value;
    }

    private static void SaveJson(string path, JsonNode node)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));

        if (File.Exists(path))
        {
            try
            {
                File.Replace(temporary, path, path + ".pre-migration", ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
            {
                File.Copy(temporary, path, overwrite: true);
                TryDelete(temporary);
                return;
            }
        }

        File.Move(temporary, path);
    }

    private sealed class CopyStats
    {
        public int Copied;
        public int Updated;
        public int Skipped;
        public int Failed;
    }

    private static void CopyDirectory(
        string source,
        string target,
        bool overwrite,
        HashSet<string>? excluded,
        CopyStats stats,
        List<string> errors)
    {
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stats.Failed++;
            errors.Add($"{target}: {ex.Message}");
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var name = Path.GetFileName(file);
                if (excluded is not null && excluded.Contains(name))
                    continue;

                var destination = Path.Combine(target, name);
                try
                {
                    var existed = File.Exists(destination);
                    if (existed && !overwrite)
                    {
                        stats.Skipped++;
                        continue;
                    }

                    File.Copy(file, destination, overwrite: true);
                    try { File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

                    if (existed)
                        stats.Updated++;
                    else
                        stats.Copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stats.Failed++;
                    errors.Add($"{file}: {ex.Message}");
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(
                    directory,
                    Path.Combine(target, Path.GetFileName(directory)),
                    overwrite, excluded, stats, errors);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stats.Failed++;
            errors.Add($"{source}: {ex.Message}");
        }
    }

    private static void MergeDirectory(
        string source,
        string target,
        bool overwrite,
        CopyStats stats,
        List<string> errors,
        Action<string>? report)
    {
        CopyDirectory(source, target, overwrite, excluded: null, stats, errors);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var order = 0;
        while (value >= 1024 && order < sizes.Length - 1)
        {
            order++;
            value /= 1024;
        }

        return $"{value:0.#} {sizes[order]}";
    }

    private static bool IsSamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent))
            return false;

        try
        {
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedParent = Path.GetFullPath(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase)
                   || normalizedPath.StartsWith(
                       normalizedParent + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vellum-Migrator/1.2");
        return client;
    }

    private sealed record ReleaseInfo(string Url, string FileName, long Size, string? Version);
    private sealed record UninstallEntry(string? DisplayName, string? InstallLocation, string? UninstallString);
}
