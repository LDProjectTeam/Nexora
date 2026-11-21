using System.Runtime.InteropServices;

namespace AutoPowerMode;

/// <summary>
/// Загрузка 3D-движка GPU по каждому процессу через счётчики производительности PDH.
/// Это лёгкий аналог диспетчера задач: один счётчик, без WMI и без опроса драйвера.
/// </summary>
internal sealed class GpuUsage : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint ERROR_SUCCESS = 0;
    private const uint PDH_CSTATUS_VALID_DATA = 0;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    private IntPtr _query;
    private IntPtr _counter;
    private bool _ready;

    public string? LastError { get; private set; }

    public bool TryInitialize()
    {
        Leave();

        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != ERROR_SUCCESS)
            {
                LastError = "PdhOpenQuery завершился ошибкой.";
                return false;
            }

            var status = PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter);
            if (status != ERROR_SUCCESS)
            {
                LastError = $"Счётчик GPU Engine недоступен (код 0x{status:X8}).";
                Leave();
                return false;
            }

            // Первый сбор нужен как база; значения появятся после следующего.
            PdhCollectQueryData(_query);
            _ready = true;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Leave();
            return false;
        }
    }

    /// <summary>Возвращает pid → суммарная загрузка 3D-движка в процентах.</summary>
    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        if (!_ready)
            return result;

        try
        {
            if (PdhCollectQueryData(_query) != ERROR_SUCCESS)
                return result;

            uint size = 0;
            uint count = 0;
            var status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
            if (status != 0x800007D2 && status != ERROR_SUCCESS) // PDH_MORE_DATA
                return result;

            if (size == 0 || count == 0)
                return result;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, buffer);
                if (status != ERROR_SUCCESS)
                    return result;

                var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
                for (var i = 0; i < count; i++)
                {
                    var itemPtr = IntPtr.Add(buffer, (int)(i * itemSize));
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(itemPtr);
                    if (item.FmtValue.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA))
                        continue;

                    var value = item.FmtValue.DoubleValue;
                    if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
                        continue;

                    var name = Marshal.PtrToStringUni(item.szName);
                    if (string.IsNullOrEmpty(name) || !name.Contains("engtype_3d", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pid = ParsePid(name);
                    if (pid <= 4)
                        continue;

                    result[pid] = Math.Min(100.0, result.GetValueOrDefault(pid) + value);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        return result;
    }

    private static int ParsePid(string instanceName)
    {
        // pid_1234_luid_0x..._eng_0_engtype_3d
        if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
            return 0;

        var end = instanceName.IndexOf('_', 4);
        if (end <= 4)
            return 0;

        return int.TryParse(instanceName.AsSpan(4, end - 4), out var pid) ? pid : 0;
    }

    public void Dispose() => Leave();

    private void Leave()
    {
        _ready = false;
        if (_query != IntPtr.Zero)
        {
            try { PdhCloseQuery(_query); } catch { /* ignore */ }
        }
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }

    // PDH_FMT_COUNTERVALUE: сначала DWORD CStatus, затем объединение по смещению 8.
    // Двойное поле обязано идти вторым, с выравниванием, иначе читается мусор.
    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
