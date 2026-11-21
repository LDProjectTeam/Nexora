using System;
using System.Runtime.InteropServices;

namespace Nexora.App.Services
{
    internal enum TaskbarProgressState : uint
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8
    }

    /// <summary>Thin owner of the native Windows taskbar progress API.</summary>
    internal sealed class WindowsTaskbarProgress : IDisposable
    {
        private ITaskbarList3? _taskbar;
        private readonly IntPtr _windowHandle;

        public WindowsTaskbarProgress(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
            if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
                return;

            try
            {
                _taskbar = (ITaskbarList3)new TaskbarListComObject();
                _taskbar.HrInit();
            }
            catch
            {
                ReleaseTaskbar();
            }
        }

        public void Update(TaskbarProgressState state, ulong completed = 0, ulong total = 0)
        {
            var taskbar = _taskbar;
            if (taskbar == null)
                return;

            try
            {
                if (state == TaskbarProgressState.NoProgress)
                {
                    taskbar.SetProgressState(_windowHandle, state);
                    return;
                }

                taskbar.SetProgressState(_windowHandle, state);
                if (total > 0)
                    taskbar.SetProgressValue(_windowHandle, Math.Min(completed, total), total);
            }
            catch
            {
                // Taskbar integration is optional (for example, Explorer can restart).
                ReleaseTaskbar();
            }
        }

        public void Dispose() => ReleaseTaskbar();

        private void ReleaseTaskbar()
        {
            var taskbar = _taskbar;
            _taskbar = null;
            if (taskbar != null && Marshal.IsComObject(taskbar))
            {
                try { Marshal.FinalReleaseComObject(taskbar); } catch { }
            }
        }

        [ComImport]
        [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
        private class TaskbarListComObject { }

        [ComImport]
        [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, TaskbarProgressState state);
        }
    }
}
