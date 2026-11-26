using System;
using System.Runtime.InteropServices;

namespace Vellum.App.Services
{
    internal enum TaskbarProgressState : uint
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8
    }

    /// <summary>
    /// Thin owner of the native Windows taskbar API. All calls must come from the UI
    /// (STA) thread; <see cref="TaskbarProgressService"/> takes care of that. If Explorer
    /// restarts or the taskbar API is unavailable the object releases its COM reference
    /// and transparently re-creates it on the next update.
    /// </summary>
    internal sealed class WindowsTaskbarProgress : IDisposable
    {
        private ITaskbarList3? _taskbar;
        private readonly IntPtr _windowHandle;
        private TaskbarProgressState? _lastState;
        private bool _disposed;
        private bool _initializationFailureLogged;

        public WindowsTaskbarProgress(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
        }

        public void Update(TaskbarProgressState state, ulong completed = 0, ulong total = 0)
        {
            if (_disposed || _windowHandle == IntPtr.Zero || !OperatingSystem.IsWindows())
                return;

            try
            {
                var taskbar = EnsureTaskbar();
                if (taskbar == null)
                    return;

                if (_lastState != state)
                {
                    taskbar.SetProgressState(_windowHandle, state);
                    _lastState = state;
                }

                if (state is TaskbarProgressState.Normal or TaskbarProgressState.Paused or TaskbarProgressState.Error
                    && total > 0)
                {
                    taskbar.SetProgressValue(_windowHandle, Math.Min(completed, total), total);
                }
            }
            catch
            {
                // Taskbar integration is optional: Explorer can restart, the shell can
                // be replaced, or the window can disappear while an operation ends.
                // Drop the COM object and retry on the next update instead of throwing.
                ReleaseTaskbar();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseTaskbar();
        }

        /// <summary>
        /// Forces the next update to set the state again. The shell re-creates the
        /// taskbar button when a window is hidden and shown from the tray, so the
        /// cached state must not suppress the re-application.
        /// </summary>
        public void InvalidateStateCache()
        {
            _lastState = null;
        }

        private ITaskbarList3? EnsureTaskbar()
        {
            if (_taskbar != null)
                return _taskbar;

            object? comObject = null;
            try
            {
                comObject = new TaskbarListComObject();
                var taskbar = (ITaskbarList3)comObject;
                if (taskbar.HrInit() != 0)
                {
                    LogInitializationFailure("ITaskbarList3.HrInit returned a failure code.");
                    ReleaseComObject(comObject);
                    return null;
                }

                _taskbar = taskbar;
                _lastState = null;
                _initializationFailureLogged = false;
                AppLog.Info("TaskbarProgress", "Windows taskbar progress indicator initialized.");
                return taskbar;
            }
            catch (Exception ex)
            {
                LogInitializationFailure(ex.Message);
                ReleaseComObject(comObject);
                return null;
            }
        }

        private void LogInitializationFailure(string reason)
        {
            // Explorer may be restarting, or the shell API may be blocked. This is
            // optional integration, so log once and keep the launcher fully usable.
            if (_initializationFailureLogged)
                return;

            _initializationFailureLogged = true;
            AppLog.Warn("TaskbarProgress",
                $"Windows taskbar progress indicator is unavailable ({reason}).");
        }

        private void ReleaseTaskbar()
        {
            var taskbar = _taskbar;
            _taskbar = null;
            _lastState = null;
            if (taskbar != null && Marshal.IsComObject(taskbar))
            {
                try { Marshal.FinalReleaseComObject(taskbar); } catch { }
            }
        }

        private static void ReleaseComObject(object? comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                try { Marshal.FinalReleaseComObject(comObject); } catch { }
            }
        }

        [ComImport]
        [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
        private class TaskbarListComObject { }

        // ITaskbarList3 inherits ITaskbarList2 and ITaskbarList. COM interop needs the
        // whole vtable flattened in declaration order, otherwise method indexes shift.
        [ComImport]
        [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            [PreserveSig]
            int HrInit();
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
