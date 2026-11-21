using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoPowerMode;

/// <summary>Иконка в трее + меню настроек. Окно скрытое, в панели задач не появляется.</summary>
internal sealed class TrayApplication : IDisposable
{
    private const string WindowClassName = "AutoPowerMode.TrayWindow";
    private const int TrayIconId = 1;
    private const int TrayTimerId = 1;

    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;
    private const int NIIF_WARNING = 0x00000002;

    private const int CmdToggleEnabled = 100;
    private const int CmdToggleAc = 101;
    private const int CmdOpenConfig = 102;
    private const int CmdOpenLog = 103;
    private const int CmdReload = 104;
    private const int CmdExit = 105;

    private readonly string _configPath;
    private readonly Engine _engine;
    private readonly NativeMethods.WndProc _wndProc;
    private IntPtr _hwnd;
    private IntPtr _iconIdle;
    private IntPtr _iconActive;
    private bool _destroyIcons;
    private IntPtr _lastIcon;
    private string _lastTip = string.Empty;
    private bool _iconAdded;
    private uint _taskbarCreatedMessage;

    public TrayApplication(string configPath, AppConfig config)
    {
        _configPath = configPath;
        _engine = new Engine(configPath, config);
        _wndProc = WndProc;
    }

    public void Run()
    {
        if (!CreateWindow())
            return;

        _iconIdle = LoadTrayIcon("idle.ico");
        _iconActive = LoadTrayIcon("active.ico");
        if (_iconIdle == IntPtr.Zero || _iconActive == IntPtr.Zero)
        {
            _destroyIcons = false; // stock icons не освобождаем
            if (_iconIdle == IntPtr.Zero)
                _iconIdle = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
            if (_iconActive == IntPtr.Zero)
                _iconActive = _iconIdle;
        }
        else
        {
            _destroyIcons = true;
        }

        _lastIcon = _iconIdle;
        AddTrayIcon();
        _engine.Start();
        NativeMethods.SetTimer(_hwnd, new IntPtr(TrayTimerId), 1500, IntPtr.Zero);
        UpdateTray(force: true);

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.KillTimer(_hwnd, new IntPtr(TrayTimerId));
        RemoveTrayIcon();
        _engine.Stop();
        _engine.Dispose();

        if (_destroyIcons)
        {
            if (_iconIdle != IntPtr.Zero) NativeMethods.DestroyIcon(_iconIdle);
            if (_iconActive != IntPtr.Zero && _iconActive != _iconIdle) NativeMethods.DestroyIcon(_iconActive);
        }

        if (_hwnd != IntPtr.Zero)
            NativeMethods.DestroyWindow(_hwnd);

        Log.Info("AutoPowerMode завершён.");
    }

    private bool CreateWindow()
    {
        var hInstance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            Log.Error("Не удалось зарегистрировать класс окна: " + Marshal.GetLastWin32Error());
            return false;
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0, WindowClassName, "AutoPowerMode", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Log.Error("Не удалось создать скрытое окно: " + Marshal.GetLastWin32Error());
            return false;
        }

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_TRAYICON)
        {
            var evt = (int)(lParam.ToInt64() & 0xFFFF);
            if (evt is NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
                ShowMenu();
            else if (evt == NativeMethods.WM_LBUTTONDBLCLK)
                OpenConfig();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_TIMER)
        {
            UpdateTray();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_ENDSESSION)
        {
            // wParam != 0 — сеанс действительно завершается, можно вернуть режим.
            if (wParam != IntPtr.Zero)
            {
                _engine.Stop();
                RemoveTrayIcon();
            }
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DESTROY)
        {
            RemoveTrayIcon();
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _iconAdded = false;
            _lastIcon = _iconIdle;
            _lastTip = string.Empty;
            AddTrayIcon();
            UpdateTray(force: true);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void AddTrayIcon()
    {
        if (_iconAdded)
            return;

        var data = BuildNotifyData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.hIcon = _lastIcon;
        data.szTip = Limit(BuildTip(), 120);

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
        {
            Log.Warn("Не удалось добавить иконку в трей: " + Marshal.GetLastWin32Error());
            return;
        }

        _iconAdded = true;
        Log.Info("Иконка добавлена в трей.");

        // Проверяем, что Windows действительно приняла иконку (без скриншотов).
        var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = TrayIconId
        };
        if (NativeMethods.Shell_NotifyIconGetRect(ref identifier, out var iconRect) == 0)
        {
            Log.Info($"Иконка трея: x={iconRect.Left}, y={iconRect.Top}, " +
                     $"размер {iconRect.Width}×{iconRect.Height}.");
        }
        else
        {
            Log.Warn("Иконка добавлена, но Shell_NotifyIconGetRect её не нашёл.");
        }
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded)
            return;

        var data = BuildNotifyData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private void UpdateTray(bool force = false)
    {
        if (!_iconAdded)
            return;

        var icon = _engine.Applied ? _iconActive : _iconIdle;
        var tip = Limit(BuildTip(), 120);
        if (!force && icon == _lastIcon && tip == _lastTip)
            return;

        var data = BuildNotifyData();
        data.uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.hIcon = icon;
        data.szTip = tip;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);

        _lastIcon = icon;
        _lastTip = tip;
    }

    private string BuildTip()
    {
        var tip = "AutoPowerMode · " + _engine.Status;
        if (_engine.Games != "нет")
            tip += " · " + _engine.Games;
        return tip;
    }

    private NativeMethods.NOTIFYICONDATA BuildNotifyData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        uCallbackMessage = NativeMethods.WM_TRAYICON,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private void ShowMenu()
    {
        NativeMethods.GetCursorPos(out var cursor);
        NativeMethods.SetForegroundWindow(_hwnd);

        var cfg = _engine.Config;
        var menu = NativeMethods.CreatePopupMenu();

        NativeMethods.AppendMenu(menu,
            (uint)(NativeMethods.MF_STRING | (cfg.Enabled ? NativeMethods.MF_CHECKED : 0)),
            new IntPtr(CmdToggleEnabled), "Игровой режим");

        NativeMethods.AppendMenu(menu,
            (uint)(NativeMethods.MF_STRING | (cfg.RequireAcPower ? NativeMethods.MF_CHECKED : 0)),
            new IntPtr(CmdToggleAc), "Только от розетки");

        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, IntPtr.Zero,
            "Сейчас: " + _engine.Status);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, IntPtr.Zero,
            "Игры: " + _engine.Games);

        if (!string.IsNullOrWhiteSpace(_engine.LastError))
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, IntPtr.Zero,
                "Ошибка: " + Limit(_engine.LastError!, 70));
        }

        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new IntPtr(CmdOpenConfig), "Открыть настройки");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new IntPtr(CmdOpenLog), "Открыть журнал");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new IntPtr(CmdReload), "Перезагрузить настройки");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new IntPtr(CmdExit), "Выход");

        var command = NativeMethods.TrackPopupMenu(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
            cursor.X, cursor.Y, 0, _hwnd, IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

        if (command != 0)
            HandleCommand(command);
    }

    private void HandleCommand(int command)
    {
        switch (command)
        {
            case CmdToggleEnabled:
                _engine.SetEnabled(!_engine.Config.Enabled);
                UpdateTray(force: true);
                break;

            case CmdToggleAc:
                _engine.SetRequireAcPower(!_engine.Config.RequireAcPower);
                UpdateTray(force: true);
                break;

            case CmdOpenConfig:
                OpenConfig();
                break;

            case CmdOpenLog:
                OpenLog();
                break;

            case CmdReload:
                if (_engine.ReloadConfig(out var error))
                {
                    ShowBalloon("Настройки перезагружены", "Автопереключение режима", NIIF_INFO);
                }
                else
                {
                    ShowBalloon("Не удалось прочитать настройки: " + (error ?? "неизвестная ошибка"),
                        "AutoPowerMode", NIIF_WARNING);
                }
                UpdateTray(force: true);
                break;

            case CmdExit:
                NativeMethods.DestroyWindow(_hwnd);
                break;
        }
    }

    private void OpenConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось открыть config.json: " + ex.Message);
            OpenInExplorer(_configPath);
        }
    }

    private void OpenLog()
    {
        try
        {
            if (File.Exists(Log.LogFile))
                Process.Start("explorer.exe", $"/select,\"{Log.LogFile}\"");
            else
                OpenInExplorer(Log.LogDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось открыть журнал: " + ex.Message);
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
            else
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch
        {
            // ignore
        }
    }

    private void ShowBalloon(string text, string title, int flags)
    {
        if (!_iconAdded)
            return;

        var data = BuildNotifyData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Limit(title, 60);
        data.szInfo = Limit(text, 240);
        data.dwInfoFlags = flags;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private IntPtr LoadTrayIcon(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "icons", fileName);
        if (!File.Exists(path))
        {
            Log.Warn("Не найден файл иконки: " + path);
            return IntPtr.Zero;
        }

        var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        if (size <= 0)
            size = 16;

        var icon = NativeMethods.LoadImage(
            IntPtr.Zero, path, NativeMethods.IMAGE_ICON, size, size, NativeMethods.LR_LOADFROMFILE);
        if (icon == IntPtr.Zero)
            Log.Warn("Не удалось загрузить иконку: " + path);

        return icon;
    }

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        _engine.Dispose();
    }
}
