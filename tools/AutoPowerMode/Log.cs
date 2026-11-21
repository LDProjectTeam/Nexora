using System.Text;

namespace AutoPowerMode;

internal static class Log
{
    private static readonly object Gate = new();
    private static string? _filePath;
    private static bool _enabled = true;
    private static bool _verbose;

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPowerMode", "logs");

    public static string LogFile => _filePath ?? Path.Combine(LogDirectory, "auto-power-mode.log");

    public static void Init(bool enabled, bool verbose)
    {
        _enabled = enabled;
        _verbose = verbose;
        _filePath = Path.Combine(LogDirectory, "auto-power-mode.log");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded();
        }
        catch
        {
            // Журнал — вспомогательная вещь, из-за него приложение падать не должно.
        }
    }

    public static void SetVerbose(bool verbose) => _verbose = verbose;

    public static void Info(string message) => Write("INFO", message, force: true);
    public static void Warn(string message) => Write("WARN", message, force: true);
    public static void Error(string message) => Write("ERROR", message, force: true);

    public static void Debug(string message)
    {
        if (_verbose)
            Write("DEBUG", message, force: false);
    }

    private static void Write(string level, string message, bool force)
    {
        if (!_enabled && !force)
            return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        if (!_enabled)
            return;

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var file = LogFile;
            if (!File.Exists(file))
                return;
            var info = new FileInfo(file);
            if (info.Length < 2 * 1024 * 1024)
                return;
            var old = file + ".1";
            if (File.Exists(old))
                File.Delete(old);
            File.Move(file, old);
        }
        catch
        {
            // ignore
        }
    }
}
