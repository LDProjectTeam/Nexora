namespace AutoPowerMode;

/// <summary>
/// Фоновый движок: опрашивает детектор, следит за питанием от сети и переключает
/// режим Lenovo только на переходах «игра началась / игра закончилась».
/// </summary>
internal sealed class Engine : IDisposable
{
    private readonly string _configPath;
    private readonly object _configGate = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private AppConfig _config;
    private Thread? _thread;
    private volatile bool _applied;
    private volatile bool _wmiAvailable;
    private volatile string _status = "Запуск…";
    private volatile string _games = "нет";
    private volatile string? _lastError;
    private volatile string _modeText = "неизвестно";
    private uint _previousMode = 2;
    private bool _disposed;

    public Engine(string configPath, AppConfig config)
    {
        _configPath = configPath;
        _config = config;
    }

    public AppConfig Config
    {
        get
        {
            lock (_configGate)
                return _config;
        }
    }

    public bool Applied => _applied;
    public bool WmiAvailable => _wmiAvailable;
    public string Status => _status;
    public string Games => _games;
    public string ModeText => _modeText;
    public string? LastError => _lastError;

    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "AutoPowerMode.Engine"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        try
        {
            _thread?.Join(5000);
        }
        catch
        {
            // ignore
        }
    }

    public void SetEnabled(bool value) => Mutate(config =>
    {
        config.Enabled = value;
    }, value ? "Игровой режим включён." : "Игровой режим выключен.");

    public void SetRequireAcPower(bool value) => Mutate(config =>
    {
        config.RequireAcPower = value;
    }, value ? "Переключение только от розетки." : "Переключение разрешено и от батареи.");

    public bool ReloadConfig(out string? error)
    {
        var config = AppConfig.LoadOrCreate(_configPath, out error);
        if (error != null && error.StartsWith("Не удалось прочитать", StringComparison.Ordinal))
            return false;

        config.Normalize(out var warning);
        if (warning != null)
            Log.Warn("Настройки: " + warning);

        lock (_configGate)
        {
            _config = config;
        }

        Log.SetVerbose(config.VerboseLogging);
        Log.Info("Настройки перезагружены из config.json.");
        return true;
    }

    private void Mutate(Action<AppConfig> change, string logMessage)
    {
        lock (_configGate)
        {
            var next = _config.Clone();
            change(next);
            next.Save(_configPath);
            _config = next;
            Log.SetVerbose(next.VerboseLogging);
        }

        Log.Info(logMessage);
    }

    private void Run()
    {
        using var detector = new GameDetector();
        var wmi = new PowerModeController();
        var wmiOk = false;
        var lastWmiProbe = DateTime.MinValue;
        var wmiErrorLogged = false;
        DateTime? gameSeenAt = null;
        DateTime? noGameAt = null;
        var lastAttempt = DateTime.MinValue;
        var lastDisplayProbe = DateTime.MinValue;
        var lastGamesText = string.Empty;
        var wasApplied = false;

        Log.Info("Движок запущен.");

        try
        {
            while (!_stop.IsSet)
            {
                var cfg = Config;
                var now = DateTime.UtcNow;

                if (!cfg.Enabled)
                {
                    if (_applied
                        && (now - lastAttempt).TotalSeconds >= cfg.RetrySeconds)
                    {
                        lastAttempt = now;
                        if (TryRestore(wmi, cfg, "игровой режим выключен"))
                            lastAttempt = DateTime.MinValue;
                    }

                    detector.Reset();
                    wmiOk = false;
                    _wmiAvailable = false;
                    gameSeenAt = null;
                    noGameAt = null;
                    _status = "Выключено";
                    _games = "нет";
                    _stop.Wait(700);
                    continue;
                }

                if ((now - lastWmiProbe).TotalSeconds >= 20)
                {
                    lastWmiProbe = now;
                    var previous = wmiOk;
                    wmiOk = wmi.TryInitialize(out var error);
                    _wmiAvailable = wmiOk;

                    if (wmiOk && !previous)
                    {
                        Log.Info("Lenovo WMI доступен: LENOVO_GAMEZONE_DATA.");
                        wmiErrorLogged = false;
                        var mode = wmi.GetMode();
                        if (mode.HasValue)
                        {
                            _modeText = PowerModeController.ModeName(mode.Value);
                            _previousMode = mode.Value;
                        }
                    }
                    else if (!wmiOk && (!wmiErrorLogged || previous))
                    {
                        Log.Warn("Lenovo WMI недоступен: " + error);
                        wmiErrorLogged = true;
                        _lastError = error;
                    }
                }

                if (wmiOk && (now - lastDisplayProbe).TotalSeconds >= 10)
                {
                    lastDisplayProbe = now;
                    var mode = wmi.GetMode();
                    if (mode.HasValue)
                    {
                        _modeText = PowerModeController.ModeName(mode.Value);
                        if (!_applied)
                            _previousMode = mode.Value;
                    }
                }

                DetectionResult detection;
                try
                {
                    detection = detector.Poll(cfg);
                }
                catch (Exception ex)
                {
                    Log.Error("Ошибка определения игры: " + ex.Message);
                    _stop.Wait(Math.Max(500, cfg.PollIntervalMs));
                    continue;
                }

                if (!string.Equals(detection.GamesText, lastGamesText, StringComparison.Ordinal))
                {
                    if (detection.Active)
                        Log.Info("Активные игры: " + detection.GamesText + ".");
                    else
                        Log.Info("Активных игр нет.");
                    lastGamesText = detection.GamesText;
                }

                var acOnline = PowerStatus.IsAcOnline();
                var acOk = !cfg.RequireAcPower || acOnline != false;
                var wantPerformance = detection.Active && acOk;

                if (wantPerformance)
                {
                    noGameAt = null;
                    gameSeenAt ??= now;

                    if (!_applied
                        && wmiOk
                        && (now - gameSeenAt.Value).TotalSeconds >= cfg.StartDelaySeconds
                        && (now - lastAttempt).TotalSeconds >= cfg.RetrySeconds)
                    {
                        lastAttempt = now;
                        var before = wmi.GetMode() ?? _previousMode;
                        _previousMode = before;

                        if (wmi.SetMode(cfg.GameMode, out var error))
                        {
                            _applied = true;
                            _lastError = null;
                            _modeText = PowerModeController.ModeName(cfg.GameMode);
                            lastDisplayProbe = now;
                            lastAttempt = DateTime.MinValue; // не задерживать обратное переключение
                            Log.Info($"Режим {PowerModeController.ModeName(before)} → " +
                                     $"{PowerModeController.ModeName(cfg.GameMode)} " +
                                     $"({detection.GamesText}).");
                        }
                        else
                        {
                            _lastError = error;
                            Log.Warn("Не удалось включить производительность: " + error);
                        }
                    }
                }
                else
                {
                    gameSeenAt = null;
                    noGameAt ??= now;

                    if (_applied
                        && (now - noGameAt.Value).TotalSeconds >= cfg.EndDelaySeconds
                        && (now - lastAttempt).TotalSeconds >= cfg.RetrySeconds)
                    {
                        lastAttempt = now;
                        if (TryRestore(wmi, cfg, detection.Active
                                ? "игра идёт, но питание не от сети"
                                : "игра завершена"))
                        {
                            lastAttempt = DateTime.MinValue;
                        }
                    }
                }

                if (!wmiOk)
                    _status = "Lenovo WMI недоступен";
                else if (_applied)
                    _status = "Производительность";
                else if (detection.Active && !acOk)
                    _status = "Ожидание · нужна розетка";
                else
                    _status = _modeText + " · ожидание";

                _games = detection.GamesText;

                if (_applied != wasApplied)
                {
                    Log.Info("Состояние: " + _status + ". Игры: " + _games + ".");
                    wasApplied = _applied;
                }

                _stop.Wait(Math.Max(500, cfg.PollIntervalMs));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Движок остановлен из-за ошибки: " + ex);
        }
        finally
        {
            if (_applied)
                TryRestore(wmi, Config, "завершение работы AutoPowerMode");

            Log.Info("Движок остановлен.");
        }
    }

    private bool TryRestore(PowerModeController wmi, AppConfig cfg, string reason)
    {
        var target = cfg.ExitMode == 0 ? _previousMode : cfg.ExitMode;
        if (!PowerModeController.IsSupportedMode(target))
            target = 2;

        var current = wmi.GetMode();
        if (current.HasValue && current.Value == target)
        {
            _applied = false;
            _modeText = PowerModeController.ModeName(target);
            Log.Info($"Режим уже {PowerModeController.ModeName(target)} ({reason}).");
            return true;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (wmi.SetMode(target, out var error))
            {
                _applied = false;
                _lastError = null;
                _modeText = PowerModeController.ModeName(target);
                Log.Info($"Режим → {PowerModeController.ModeName(target)} ({reason}).");
                return true;
            }

            Log.Warn($"Не удалось вернуть режим {PowerModeController.ModeName(target)} " +
                     $"(попытка {attempt}): {error}");
            _lastError = error;
            Thread.Sleep(300);
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _stop.Dispose();
    }
}
