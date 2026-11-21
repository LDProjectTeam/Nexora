using System.Text;

namespace AutoPowerMode;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\AutoPowerMode.SingleInstance", out var createdNew);
        if (!createdNew)
            return 0;

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = AppConfig.LoadOrCreate(configPath, out var configError);
        config.Normalize(out var normalizationWarning);

        Log.Init(enabled: true, verbose: config.VerboseLogging);
        if (!string.IsNullOrWhiteSpace(configError))
            Log.Warn(configError);
        if (!string.IsNullOrWhiteSpace(normalizationWarning))
            Log.Warn("Настройки: " + normalizationWarning);

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        Log.Info($"AutoPowerMode {version} запущен. Конфиг: {configPath}");

        if (args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiagnostics(config);
            return 0;
        }

        using var application = new TrayApplication(configPath, config);
        application.Run();
        return 0;
    }

    private static void RunDiagnostics(AppConfig config)
    {
        using var detector = new GameDetector();

        // Первый опрос инициализирует счётчик GPU, второй — снимает реальные значения.
        detector.Poll(config);
        Thread.Sleep(2200);
        var report = detector.BuildDiagnostics(config);

        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            var path = Path.Combine(Log.LogDirectory, "diagnostics.txt");
            File.WriteAllText(path, report, Encoding.UTF8);
            Log.Info("Диагностика сохранена: " + path);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить диагностику: " + ex.Message);
        }
    }
}
