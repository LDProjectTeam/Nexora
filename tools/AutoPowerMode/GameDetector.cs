using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoPowerMode;

internal sealed class DetectionResult
{
    public bool Active { get; init; }
    public IReadOnlyList<string> Games { get; init; } = Array.Empty<string>();

    public string GamesText => Games.Count == 0 ? "нет" : string.Join(", ", Games);

    public static DetectionResult Idle { get; } = new();
}

/// <summary>
/// Определяет игру по совокупности признаков:
///  - окно на переднем плане занимает большую часть монитора;
///  - процесс реально нагружает 3D-движок дискретной GPU (счётчик «GPU Engine»);
///  - либо Windows сообщает о полноэкранном D3D-приложении.
/// Список исключений и принудительный список процессов настраиваются в config.json.
/// </summary>
internal sealed class GameDetector : IDisposable
{
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int SessionWindowGraceSeconds = 60;

    /// <summary>
    /// Если окно маленькое (игра в окне), процесс должен занимать почти весь
    /// 3D-движок GPU — иначе это обычное приложение с лёгкой нагрузкой.
    /// </summary>
    private const double HeavyWindowedGpuPercent = 50.0;

    private static readonly HashSet<string> IgnoredWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "DV2ControlHost",
        "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "SysShadow"
    };

    private readonly GpuUsage _gpu = new();
    private readonly Dictionary<int, Session> _sessions = new();
    private bool _gpuReady;
    private bool _gpuErrorLogged;
    private DateTime _lastGpuInit = DateTime.MinValue;

    public string? GpuError => _gpu.LastError;
    public bool GpuReady => _gpuReady;

    public DetectionResult Poll(AppConfig config)
    {
        var now = DateTime.UtcNow;

        if (!_gpuReady && (now - _lastGpuInit).TotalSeconds >= 60)
        {
            _lastGpuInit = now;
            _gpuReady = _gpu.TryInitialize();
            if (_gpuReady)
            {
                _gpuErrorLogged = false;
                Log.Info("Счётчик GPU Engine доступен: определение игр по нагрузке GPU включено.");
            }
            else if (!_gpuErrorLogged)
            {
                _gpuErrorLogged = true;
                Log.Warn($"Счётчик GPU Engine недоступен ({_gpu.LastError}). " +
                         "Остаётся определение по полноэкранному D3D; повтор через 60 секунд.");
            }
        }

        var gpu = _gpuReady ? _gpu.Sample() : new Dictionary<int, double>();
        var processes = SnapshotProcesses();
        var excluded = new HashSet<string>(config.ExcludeProcesses, StringComparer.OrdinalIgnoreCase);
        var forcedNames = new HashSet<string>(config.ExtraGameProcesses, StringComparer.OrdinalIgnoreCase);

        var forcedPids = new HashSet<int>();
        foreach (var pair in processes)
        {
            if (forcedNames.Contains(pair.Value) && !excluded.Contains(pair.Value))
                forcedPids.Add(pair.Key);
        }

        var windows = SnapshotWindows(processes, excluded);
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundRoot = foreground != IntPtr.Zero
            ? NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT)
            : IntPtr.Zero;

        NativeMethods.SHQueryUserNotificationState(out var notificationState);

        var candidates = new HashSet<int>();
        var seenWindows = new HashSet<int>();

        foreach (var window in windows)
        {
            if (excluded.Contains(window.ProcessName))
                continue;

            var isKnown = _sessions.ContainsKey(window.Pid);
            if (isKnown)
                seenWindows.Add(window.Pid);

            if (!window.HasUsableRect || window.IsMinimized)
                continue;

            var gpuPercent = gpu.GetValueOrDefault(window.Pid);
            var enoughWindow = window.CoveragePercent >= config.MinWindowCoveragePercent;
            var heavyGpu = gpuPercent >= Math.Max(config.GpuThresholdPercent, HeavyWindowedGpuPercent);

            // Маленькое окно принимаем только при очень высокой нагрузке GPU.
            if (!enoughWindow && !(heavyGpu && window.CoveragePercent >= 10))
                continue;

            var isForeground = window.Root == foregroundRoot;
            if (!isForeground && !isKnown)
                continue;

            var fullscreenD3D = notificationState == QUNS_RUNNING_D3D_FULL_SCREEN
                                && window.CoveragePercent >= 85;
            var gpuEvidence = enoughWindow
                ? gpuPercent >= config.GpuThresholdPercent
                : heavyGpu;

            // Если счётчики GPU почему-то недоступны, остаётся грубый, но полезный
            // признак: полноэкранное приложение на переднем плане.
            var fullscreenFallback = !_gpuReady
                                     && notificationState is 2 or 3 or 4
                                     && window.CoveragePercent >= 95;

            var isCandidate = gpuEvidence || fullscreenD3D || fullscreenFallback;

            if (config.VerboseLogging && isForeground)
            {
                Log.Debug($"Окно: {window.ProcessName} (pid {window.Pid}), " +
                          $"покрытие {window.CoveragePercent:F0}%, GPU3D {gpuPercent:F1}%, " +
                          $"D3D-fullscreen={fullscreenD3D}, fallback={fullscreenFallback}, кандидат={isCandidate}");
            }

            if (!isCandidate)
                continue;

            candidates.Add(window.Pid);
            seenWindows.Add(window.Pid);
        }

        foreach (var pid in forcedPids)
        {
            candidates.Add(pid);
            seenWindows.Add(pid);
        }

        foreach (var pid in candidates)
        {
            if (!_sessions.ContainsKey(pid))
            {
                var name = processes.GetValueOrDefault(pid, $"pid {pid}");
                _sessions[pid] = new Session(name, now);
                Log.Info($"Обнаружена игра: {name} (pid {pid}).");            }
        }

        foreach (var pid in seenWindows)
        {
            if (_sessions.TryGetValue(pid, out var session))
                session.LastWindowSeen = now;
        }

        foreach (var pid in forcedPids)
        {
            if (_sessions.TryGetValue(pid, out var session))
                session.LastWindowSeen = now;
        }

        // Игра, ушедшая в фон или свёрнутая, удерживается, пока процесс
        // продолжает нагружать 3D-движок GPU.
        foreach (var pair in gpu)
        {
            if (pair.Value >= config.GpuThresholdPercent
                && _sessions.TryGetValue(pair.Key, out var gpuSession))
            {
                gpuSession.LastGpuSeen = now;
            }
        }

        foreach (var pid in _sessions.Keys.ToList())
        {
            if (!processes.ContainsKey(pid))
            {
                Log.Info($"Игра завершилась: {_sessions[pid].Name} (pid {pid}).");
                _sessions.Remove(pid);
                continue;
            }

            var session = _sessions[pid];
            var windowIdle = (now - session.LastWindowSeen).TotalSeconds > SessionWindowGraceSeconds;
            var gpuIdle = (now - session.LastGpuSeen).TotalSeconds > SessionWindowGraceSeconds;
            if (!forcedPids.Contains(pid) && windowIdle && gpuIdle)
            {
                Log.Info($"Игра больше не показывает окно: {session.Name} (pid {pid}).");
                _sessions.Remove(pid);
            }
        }

        var games = _sessions.Values
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DetectionResult { Active = games.Length > 0, Games = games };
    }

    public void Reset()
    {
        _sessions.Clear();
    }

    private static Dictionary<int, string> SnapshotProcesses()
    {
        var result = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                result[process.Id] = process.ProcessName;
            }
            catch
            {
                // защищённые процессы пропускаем
            }
            finally
            {
                process.Dispose();
            }
        }
        return result;
    }

    private static List<WindowInfo> SnapshotWindows(Dictionary<int, string> processes, HashSet<string> excluded)
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true;

            if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd)
                return true;

            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
                return true;

            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (IgnoredWindowClasses.Contains(className.ToString()))
                return true;

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                && cloaked != 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pidValue);
            var pid = unchecked((int)pidValue);
            if (pid <= 4)
                return true;

            if (!processes.TryGetValue(pid, out var processName) || excluded.Contains(processName))
                return true;

            var isMinimized = NativeMethods.IsIconic(hwnd);
            var hasRect = NativeMethods.GetWindowRect(hwnd, out var rect);

            var coverage = 0.0;
            if (hasRect && !isMinimized)
                coverage = GetCoveragePercent(hwnd, rect);

            var title = string.Empty;
            if (hasRect)
            {
                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            result.Add(new WindowInfo
            {
                Hwnd = hwnd,
                Root = hwnd,
                Pid = pid,
                ProcessName = processName,
                Title = title,
                CoveragePercent = coverage,
                IsMinimized = isMinimized,
                HasUsableRect = hasRect
            });

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static double GetCoveragePercent(IntPtr hwnd, NativeMethods.RECT windowRect)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero)
            return 0;

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return 0;

        var mon = info.rcMonitor;
        var left = Math.Max(windowRect.Left, mon.Left);
        var top = Math.Max(windowRect.Top, mon.Top);
        var right = Math.Min(windowRect.Right, mon.Right);
        var bottom = Math.Min(windowRect.Bottom, mon.Bottom);
        if (right <= left || bottom <= top)
            return 0;

        var intersection = (long)(right - left) * (bottom - top);
        var monitorArea = mon.Area;
        return monitorArea <= 0 ? 0 : Math.Min(100.0, intersection * 100.0 / monitorArea);
    }

    /// <summary>Диагностический отчёт: что видит детектор в данный момент.</summary>
    public string BuildDiagnostics(AppConfig config)
    {
        var gpu = _gpuReady ? _gpu.Sample() : new Dictionary<int, double>();
        var processes = SnapshotProcesses();
        var excluded = new HashSet<string>(config.ExcludeProcesses, StringComparer.OrdinalIgnoreCase);
        var windows = SnapshotWindows(processes, excluded);
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundRoot = foreground != IntPtr.Zero
            ? NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT)
            : IntPtr.Zero;
        NativeMethods.SHQueryUserNotificationState(out var state);

        var sb = new StringBuilder();
        sb.AppendLine($"AutoPowerMode diagnostics {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"GPU Engine counter: {(_gpuReady ? "OK" : "FAIL " + _gpu.LastError)}");
        sb.AppendLine($"SHQueryUserNotificationState: {state}");
        sb.AppendLine($"Foreground HWND: 0x{foregroundRoot.ToInt64():X}");
        sb.AppendLine();
        sb.AppendLine("pid      GPU3D%   coverage%  fg  minimized  process / title");
        sb.AppendLine("----------------------------------------------------------------------------");

        foreach (var w in windows.OrderByDescending(w => w.CoveragePercent))
        {
            sb.AppendLine($"{w.Pid,-8} {gpu.GetValueOrDefault(w.Pid),7:F1}   {w.CoveragePercent,8:F0}  " +
                          $"{(w.Root == foregroundRoot ? "Y" : "n"),-2}  {(w.IsMinimized ? "Y" : "n"),-9}  " +
                          $"{w.ProcessName} / {Truncate(w.Title, 60)}");
        }

        sb.AppendLine();
        sb.AppendLine("Файл также сохранён рядом с журналом.");
        return sb.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _gpu.Dispose();

    private sealed class Session
    {
        public Session(string name, DateTime lastWindowSeen)
        {
            Name = name;
            LastWindowSeen = lastWindowSeen;
            LastGpuSeen = lastWindowSeen;
        }

        public string Name { get; }
        public DateTime LastWindowSeen { get; set; }
        public DateTime LastGpuSeen { get; set; }
    }

    private sealed class WindowInfo
    {
        public IntPtr Hwnd { get; init; }
        public IntPtr Root { get; init; }
        public int Pid { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public double CoveragePercent { get; init; }
        public bool IsMinimized { get; init; }
        public bool HasUsableRect { get; init; }
    }
}
