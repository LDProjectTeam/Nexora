using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using Vellum.Core.Models;
using Vellum.Core.Services;

namespace Vellum.App.Services
{
    /// <summary>
    /// Application-wide bridge between long-running operations and the Windows taskbar.
    /// Operations report through <see cref="Vellum.Core.Models.OperationProgressSnapshot"/>
    /// (a loader/installer agnostic contract); this service aggregates them, marshals
    /// native calls to the WPF dispatcher thread and degrades silently when the Windows
    /// shell API is unavailable.
    /// </summary>
    public interface ITaskbarProgressService : IDisposable
    {
        /// <summary>Binds the service to the main window handle once it exists.</summary>
        void Attach(IntPtr windowHandle);

        /// <summary>
        /// Replaces the set of currently running operations. The service owns the
        /// aggregation, so callers can simply pass every active card/operation.
        /// </summary>
        void SetOperations(IReadOnlyCollection<OperationProgressSnapshot> operations);

        /// <summary>Clears the indicator (no active operations).</summary>
        void Reset();

        /// <summary>
        /// Re-applies the current state, for example after the taskbar button is
        /// re-created when the window is restored from the tray.
        /// </summary>
        void Reapply();
    }

    internal sealed class TaskbarProgressService : ITaskbarProgressService
    {
        private static readonly OperationProgressSnapshot[] NoOperations =
            Array.Empty<OperationProgressSnapshot>();

        private readonly Dispatcher _dispatcher;
        private readonly object _sync = new();

        private OperationProgressSnapshot[] _operations = NoOperations;
        private WindowsTaskbarProgress? _native;
        private IntPtr _windowHandle;
        private int _updateScheduled;
        private bool _disposed;
        private string? _preferredOperationId;
        private OperationProgressDisplayState _lastLoggedState = OperationProgressDisplayState.None;

        public TaskbarProgressService()
        {
            // Constructed by DI on the UI thread. The fallback keeps unit-like usage
            // and designer scenarios from crashing.
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public void Attach(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || _disposed)
                return;

            RunOnDispatcher(() =>
            {
                if (_disposed)
                    return;

                _windowHandle = windowHandle;
                _native?.Dispose();
                _native = new WindowsTaskbarProgress(windowHandle);

                OperationProgressSnapshot[] operations;
                lock (_sync)
                    operations = _operations;
                ApplyCore(operations);
            });
        }

        public void SetOperations(IReadOnlyCollection<OperationProgressSnapshot> operations)
        {
            if (_disposed)
                return;

            OperationProgressSnapshot[] copy;
            if (operations.Count == 0)
            {
                copy = NoOperations;
            }
            else
            {
                copy = new OperationProgressSnapshot[operations.Count];
                var index = 0;
                foreach (var operation in operations)
                    copy[index++] = operation;
            }

            lock (_sync)
                _operations = copy;

            ScheduleApply();
        }

        public void Reset()
        {
            if (_disposed)
                return;

            lock (_sync)
                _operations = NoOperations;
            _preferredOperationId = null;

            ScheduleApply();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            lock (_sync)
                _operations = NoOperations;
            _preferredOperationId = null;

            RunOnDispatcher(() =>
            {
                _native?.Dispose();
                _native = null;
                _windowHandle = IntPtr.Zero;
            });
        }

        public void Reapply()
        {
            if (_disposed)
                return;

            RunOnDispatcher(() =>
            {
                OperationProgressSnapshot[] operations;
                lock (_sync)
                    operations = _operations;

                _native?.InvalidateStateCache();
                ApplyCore(operations);
            });
        }

        private void ScheduleApply()
        {
            if (_disposed)
                return;

            if (_dispatcher.CheckAccess())
            {
                ApplyNow();
                return;
            }

            // Many progress reports can arrive between dispatcher turns (parallel
            // downloads, extraction loops). Coalesce them into one native update.
            if (Interlocked.Exchange(ref _updateScheduled, 1) != 0)
                return;

            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    Interlocked.Exchange(ref _updateScheduled, 0);
                    ApplyNow();
                }));
            }
            catch (InvalidOperationException)
            {
                // The dispatcher is shutting down; there is nothing left to draw.
                Interlocked.Exchange(ref _updateScheduled, 0);
            }
        }

        private void ApplyNow()
        {
            if (_disposed)
                return;

            OperationProgressSnapshot[] operations;
            lock (_sync)
                operations = _operations;

            ApplyCore(operations);
        }

        private void ApplyCore(IReadOnlyCollection<OperationProgressSnapshot> operations)
        {
            var native = _native;
            if (native == null || _windowHandle == IntPtr.Zero)
                return;

            var aggregate = OperationProgressAggregator.Aggregate(operations, _preferredOperationId);
            _preferredOperationId = aggregate.OperationId;

            // State changes are rare (start, pause, resume, failure, completion) and
            // make field diagnostics much easier than anonymous native calls.
            if (aggregate.State != _lastLoggedState)
            {
                _lastLoggedState = aggregate.State;
                AppLog.Info("TaskbarProgress",
                    $"Indicator state: {aggregate.State} ({aggregate.OperationId ?? "none"}) {aggregate.Completed}/{aggregate.Total}.");
            }

            switch (aggregate.State)
            {
                case OperationProgressDisplayState.None:
                    native.Update(TaskbarProgressState.NoProgress);
                    break;

                case OperationProgressDisplayState.Indeterminate:
                    native.Update(TaskbarProgressState.Indeterminate);
                    break;

                case OperationProgressDisplayState.Normal:
                    native.Update(TaskbarProgressState.Normal, aggregate.Completed, aggregate.Total);
                    break;

                case OperationProgressDisplayState.Paused:
                    native.Update(TaskbarProgressState.Paused, aggregate.Completed, aggregate.Total);
                    break;

                case OperationProgressDisplayState.Error:
                    native.Update(TaskbarProgressState.Error, aggregate.Completed, aggregate.Total);
                    break;

                default:
                    native.Update(TaskbarProgressState.NoProgress);
                    break;
            }
        }

        private void RunOnDispatcher(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                try { action(); } catch { /* optional UI integration must never throw */ }
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Send, action);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher already shut down.
            }
        }
    }
}
