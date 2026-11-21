namespace AutoPowerMode;

internal static class PowerStatus
{
    public static bool? IsAcOnline()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
            return null;

        return status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null
        };
    }
}
