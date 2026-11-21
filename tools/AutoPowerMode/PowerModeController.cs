using System.Management;

namespace AutoPowerMode;

/// <summary>
/// Управление режимом Lenovo так же, как это делает Fn+Q / Lenovo Vantage:
/// методы SetSmartFanMode / GetSmartFanMode класса LENOVO_GAMEZONE_DATA.
/// Значения: 1 = тихий, 2 = сбалансированный, 3 = производительность, 255 = пользовательский.
/// </summary>
internal sealed class PowerModeController
{
    private const string Scope = @"root\WMI";
    private const string Query = "SELECT * FROM LENOVO_GAMEZONE_DATA";

    public string? LastError { get; private set; }

    public static bool IsSupportedMode(uint mode) => mode is 1 or 2 or 3 or 255;

    public static string ModeName(uint mode) => mode switch
    {
        1 => "Тихий",
        2 => "Сбалансированный",
        3 => "Производительность",
        255 => "Пользовательский",
        _ => $"Режим {mode}"
    };

    public bool TryInitialize(out string? error)
    {
        error = null;
        LastError = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, Query);
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    try
                    {
                        using var outParams = obj.InvokeMethod("GetSmartFanMode", Array.Empty<object>()) as ManagementBaseObject;
                        if (outParams?["Data"] != null)
                        {
                            error = null;
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        error = ex.Message;
                    }

                    // Класс есть, но метод недоступен — это всё равно Lenovo GameZone.
                    error = null;
                    return true;
                }
            }

            error = "Класс LENOVO_GAMEZONE_DATA не найден. Это не Lenovo Gaming или не установлен драйвер Lenovo GameZone.";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            error = ex.Message;
            return false;
        }
    }

    public uint? GetMode()
    {
        LastError = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, Query);
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    using var outParams = obj.InvokeMethod("GetSmartFanMode", Array.Empty<object>()) as ManagementBaseObject;
                    var data = outParams?["Data"];
                    if (data != null)
                        return Convert.ToUInt32(data);
                }
            }

            LastError = "WMI не вернул текущий режим.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        return null;
    }

    public bool SetMode(uint mode, out string? error)
    {
        error = null;
        LastError = null;

        if (!IsSupportedMode(mode))
        {
            error = $"Режим {mode} не входит в список поддерживаемых.";
            return false;
        }

        try
        {
            var invoked = false;
            using (var searcher = new ManagementObjectSearcher(Scope, Query))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    using (var inParams = obj.GetMethodParameters("SetSmartFanMode"))
                    {
                        inParams["Data"] = mode;
                        using var outParams = obj.InvokeMethod("SetSmartFanMode", inParams, null);
                        invoked = true;
                    }
                    break;
                }
            }

            if (!invoked)
            {
                error = "Не найден экземпляр LENOVO_GAMEZONE_DATA.";
                return false;
            }

            // Даём прошивке применить режим и проверяем, что он действительно встал.
            Thread.Sleep(400);
            var actual = GetMode();
            if (actual.HasValue && actual.Value != mode)
            {
                error = $"Прошивка не приняла режим: запрошен {mode}, сейчас {actual.Value}.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            error = ex.Message;
            return false;
        }
    }
}
