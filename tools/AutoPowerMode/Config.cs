using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace AutoPowerMode;

internal sealed class AppConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Переключать на производительность только при питании от сети.</summary>
    public bool RequireAcPower { get; set; } = true;

    /// <summary>1 = тихий, 2 = сбалансированный, 3 = производительность, 255 = пользовательский.</summary>
    public uint GameMode { get; set; } = 3;

    /// <summary>Режим при выходе из игры. 0 = вернуть тот, что был до игры.</summary>
    public uint ExitMode { get; set; } = 2;

    public int StartDelaySeconds { get; set; } = 3;
    public int EndDelaySeconds { get; set; } = 6;
    public int PollIntervalMs { get; set; } = 2000;

    /// <summary>Порог загрузки 3D-движка GPU конкретным процессом, %.</summary>
    public double GpuThresholdPercent { get; set; } = 7.0;

    /// <summary>Какую часть экрана должно занимать окно игры, %.</summary>
    public double MinWindowCoveragePercent { get; set; } = 60.0;

    public int RetrySeconds { get; set; } = 30;
    public bool VerboseLogging { get; set; }

    /// <summary>Процессы, которые всегда считаются игрой (имена без .exe).</summary>
    public List<string> ExtraGameProcesses { get; set; } = new();

    /// <summary>Процессы, которые никогда не считаются игрой (имена без .exe).</summary>
    public List<string> ExcludeProcesses { get; set; } = new();

    [JsonIgnore]
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig LoadOrCreate(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            var defaults = CreateDefault();
            defaults.Save(path);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefault();
            config.Normalize(out var warning);
            error = warning;
            return config;
        }
        catch (Exception ex)
        {
            error = $"Не удалось прочитать {Path.GetFileName(path)}: {ex.Message}. Используются настройки по умолчанию.";
            return CreateDefault();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefault();
    }

    public void Normalize(out string? warning)
    {
        var warnings = new List<string>();

        if (GameMode is not (1 or 2 or 3 or 255))
        {
            warnings.Add($"GameMode={GameMode} не поддерживается, использую 3 (производительность).");
            GameMode = 3;
        }

        if (ExitMode is not (0 or 1 or 2 or 3 or 255))
        {
            warnings.Add($"ExitMode={ExitMode} не поддерживается, использую 2 (сбалансированный).");
            ExitMode = 2;
        }

        var clamped = PollIntervalMs;
        PollIntervalMs = Math.Clamp(PollIntervalMs, 1000, 15000);
        if (clamped != PollIntervalMs)
            warnings.Add($"PollIntervalMs скорректирован до {PollIntervalMs}.");

        clamped = StartDelaySeconds;
        StartDelaySeconds = Math.Clamp(StartDelaySeconds, 0, 120);
        if (clamped != StartDelaySeconds)
            warnings.Add($"StartDelaySeconds скорректирован до {StartDelaySeconds}.");

        clamped = EndDelaySeconds;
        EndDelaySeconds = Math.Clamp(EndDelaySeconds, 0, 120);
        if (clamped != EndDelaySeconds)
            warnings.Add($"EndDelaySeconds скорректирован до {EndDelaySeconds}.");

        var clampedGpu = GpuThresholdPercent;
        GpuThresholdPercent = Math.Clamp(GpuThresholdPercent, 0.5, 100);
        if (Math.Abs(clampedGpu - GpuThresholdPercent) > 0.001)
            warnings.Add($"GpuThresholdPercent скорректирован до {GpuThresholdPercent}.");

        clampedGpu = MinWindowCoveragePercent;
        MinWindowCoveragePercent = Math.Clamp(MinWindowCoveragePercent, 10, 100);
        if (Math.Abs(clampedGpu - MinWindowCoveragePercent) > 0.001)
            warnings.Add($"MinWindowCoveragePercent скорректирован до {MinWindowCoveragePercent}.");

        clamped = RetrySeconds;
        RetrySeconds = Math.Clamp(RetrySeconds, 5, 600);
        if (clamped != RetrySeconds)
            warnings.Add($"RetrySeconds скорректирован до {RetrySeconds}.");

        ExcludeProcesses = NormalizeNames(ExcludeProcesses);
        ExtraGameProcesses = NormalizeNames(ExtraGameProcesses);

        // Свой процесс и системные оболочки исключаем всегда.
        foreach (var mandatory in new[] { "autopowermode", "explorer" })
        {
            if (!ExcludeProcesses.Contains(mandatory, StringComparer.OrdinalIgnoreCase))
                ExcludeProcesses.Add(mandatory);
        }

        warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    public static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Trim();
    }

    private static List<string> NormalizeNames(List<string>? names)
    {
        var result = new List<string>();
        foreach (var raw in names ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var name = NormalizeName(raw);
            if (name.Length == 0 || result.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            result.Add(name);
        }
        return result;
    }

    private static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        config.ExcludeProcesses = DefaultExclusions();
        config.Normalize(out _);
        return config;
    }

    private static List<string> DefaultExclusions() => new()
    {
        "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
        "ApplicationFrameHost", "SystemSettings", "LockApp", "LogonUI", "Taskmgr",
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "yandex", "chromium", "arc",
        "vlc", "mpv", "mpc-hc64", "mpc-be64", "potplayermini64", "potplayermini", "kmplayer", "wmplayer", "spotify",
        "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "agent", "origin", "eaapp", "eadesktop",
        "ubisoftconnect", "upc", "uplay", "rgl", "galaxylauncher", "goggalaxy", "playnite",
        "nexora", "nexora.torrentengine",
        "discord", "telegram", "whatsapp", "zoom", "teams", "ms-teams", "slack",
        "code", "devenv", "idea64", "pycharm64", "rider64", "webstorm64",
        "obs64", "obs32", "photoshop", "illustrator", "afterfx", "adobe premiere pro", "resolve", "blender",
        "winword", "excel", "powerpnt", "acrobat", "acrord32",
        "autopowermode"
    };
}
