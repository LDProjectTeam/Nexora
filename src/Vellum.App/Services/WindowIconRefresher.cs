using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Vellum.App.Services
{
    /// <summary>
    /// Re-applies the executable icon to the already created window.
    /// <para>
    /// Windows builds the taskbar button from the icon that is available while the
    /// window is being created. When an update replaces the .ico, the shell may keep
    /// the previous taskbar icon for that executable even though WPF already exposes
    /// the new one. Sending WM_SETICON once after the window is shown makes the
    /// taskbar pick up the new artwork immediately instead of waiting for an Explorer
    /// restart or a re-pin.
    /// </para>
    /// </summary>
    internal sealed class WindowIconRefresher : IDisposable
    {
        private const int WmSetIcon = 0x0080;
        private const int IconSmall = 0;
        private const int IconBig = 1;
        private const int IconSmall2 = 2;

        private IntPtr _largeIcon;
        private IntPtr _smallIcon;
        private bool _applied;
        private bool _disposed;

        public void Apply(Window window)
        {
            if (_disposed || _applied || window == null)
                return;

            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed)
                    return;

                try
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    if (handle == IntPtr.Zero)
                        return;

                    var executable = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
                        return;

                    // `dotnet run` uses dotnet.exe as the host; it has no Vellum icon.
                    if (string.Equals(
                            Path.GetFileNameWithoutExtension(executable),
                            "dotnet",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (ExtractIconEx(executable, 0, out var large, out var small, 1) == 0)
                        return;

                    if (large == IntPtr.Zero && small == IntPtr.Zero)
                        return;

                    if (large == IntPtr.Zero)
                        large = small;
                    if (small == IntPtr.Zero)
                        small = large;

                    ReleaseIcons();
                    _largeIcon = large;
                    _smallIcon = small;
                    _applied = true;

                    _ = SendMessage(handle, WmSetIcon, (IntPtr)IconBig, large);
                    _ = SendMessage(handle, WmSetIcon, (IntPtr)IconSmall, small);
                    _ = SendMessage(handle, WmSetIcon, (IntPtr)IconSmall2, small);
                }
                catch
                {
                    // The window keeps WPF's icon if the shell call is unavailable.
                }
            }));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseIcons();
        }

        private void ReleaseIcons()
        {
            if (_largeIcon != IntPtr.Zero)
            {
                try { DestroyIcon(_largeIcon); } catch { }
                _largeIcon = IntPtr.Zero;
            }

            if (_smallIcon != IntPtr.Zero)
            {
                try { DestroyIcon(_smallIcon); } catch { }
                _smallIcon = IntPtr.Zero;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(
            string file,
            int iconIndex,
            out IntPtr largeIcon,
            out IntPtr smallIcon,
            uint iconCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
