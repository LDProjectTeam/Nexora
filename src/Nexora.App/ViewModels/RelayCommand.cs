using System;
using System.Windows.Input;
using System.Windows.Threading;
using System.Threading.Tasks;
using Nexora.App.Services;

namespace Nexora.App.ViewModels
{
    /// <summary>
    /// Synchronous relay command. CanExecuteChanged is wired to CommandManager.RequerySuggested
    /// so WPF re-evaluates it automatically after routed events (button clicks, focus changes, etc.).
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute())
        { }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);
        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Async relay command with LOCAL CanExecuteChanged — does NOT hook into the global
    /// CommandManager.RequerySuggested. This prevents UI freezes when hundreds of cards are shown.
    /// Call RaiseCanExecuteChanged() explicitly when the command's availability changes.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute())
        { }

        public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        // Local event — not wired to CommandManager.RequerySuggested
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            !_isExecuting && (_canExecute == null || _canExecute(parameter));

        public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

        public async Task ExecuteAsync(object? parameter = null)
        {
            if (!CanExecute(parameter)) return;
            _isExecuting = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute(parameter);
            }
            catch (OperationCanceledException)
            {
                // Pausing a download or closing a window cancels in-flight work;
                // this is expected and must not be reported as a failure.
            }
            catch (Exception ex)
            {
                // Previously this exception vanished (fire-and-forget task) and the
                // UI just looked unresponsive. Log it and tell the user.
                ErrorReporter.Report("Command", ex);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            // Must fire on the UI thread
            if (System.Windows.Application.Current?.Dispatcher is Dispatcher d && !d.CheckAccess())
                d.BeginInvoke(RaiseOnUiThread);
            else
                RaiseOnUiThread();
        }

        private void RaiseOnUiThread() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
