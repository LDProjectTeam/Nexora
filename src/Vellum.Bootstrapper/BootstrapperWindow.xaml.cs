using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Vellum.Core.Models;
using Vellum.Core.Services;

namespace Vellum.Bootstrapper
{
    public partial class BootstrapperWindow : Window
    {
        private readonly BootstrapperViewModel _viewModel = new();
        private readonly Func<IProgress<BootstrapperProgress>, CancellationToken, Task<BootstrapperRunResult>> _run;
        private readonly Func<Task<BootstrapperRunResult>> _launchCurrent;
        private readonly Action _shutdown;
        private readonly BootstrapperDisplayCoordinator _display = new();
        private readonly CancellationTokenSource _cts = new();
        private bool _started;
        private bool _running;
        private bool _closing;

        public BootstrapperWindow(
            Func<IProgress<BootstrapperProgress>, CancellationToken, Task<BootstrapperRunResult>> run,
            Func<Task<BootstrapperRunResult>> launchCurrent,
            Action shutdown)
        {
            InitializeComponent();
            _run = run;
            _launchCurrent = launchCurrent;
            _shutdown = shutdown;
            DataContext = _viewModel;
            CloseButton.Content = BootstrapperText.Get("Close");
            RetryButton.Content = BootstrapperText.Get("Retry");
            LaunchButton.Content = BootstrapperText.Get("StartPrevious");
            Loaded += OnLoaded;
        }

        public BootstrapperViewModel ViewModel => _viewModel;

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_started)
                return;

            _started = true;
            _display.MarkUiReady();
            BootstrapperLog.Write("Window ready; workflow starting.");
            await RunWorkflowAsync();
        }

        private async Task RunWorkflowAsync()
        {
            if (_running || _closing)
                return;

            _running = true;
            var attemptId = _display.BeginAttempt();
            _viewModel.BeginRun();

            var progress = new Progress<BootstrapperProgress>(p =>
            {
                // Progress<T> posts to the UI thread. Ignore updates left over from
                // an older attempt or queued behind a final result.
                if (_display.TryApplyProgress(p, attemptId))
                    _viewModel.Apply(p);
            });

            try
            {
                var result = await _run(progress, _cts.Token);
                if (result.Success)
                {
                    if (!_display.CompleteSuccess(result))
                        return;

                    BootstrapperLog.Write(
                        $"Workflow result: success={result.Success}, launched={result.Launched}, " +
                        $"canLaunch={result.CanLaunchCurrent}, error={result.ErrorMessage}");
                    _viewModel.SetStarting();

                    try
                    {
                        await _display.WaitForMinimumDisplayTimeAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!_closing)
                    {
                        BootstrapperLog.Write("Minimum display time elapsed; closing.");
                        Close();
                    }
                    return;
                }

                if (!_display.CompleteFailure(result))
                    return;

                BootstrapperLog.Write(
                    $"Workflow result: success={result.Success}, launched={result.Launched}, " +
                    $"canLaunch={result.CanLaunchCurrent}, error={result.ErrorMessage}");
                _viewModel.SetFailure(result.ErrorMessage ?? "Unknown error",
                    result.CanRetry, result.CanLaunchCurrent);
            }
            catch (OperationCanceledException)
            {
                if (_display.Cancel() && !_closing && IsVisible)
                    _viewModel.SetFailure("Cancelled.", true, false);
            }
            catch (Exception ex)
            {
                if (_display.Fail("Bootstrapper failed: " + ex.Message, true, true)
                    && !_closing && IsVisible)
                {
                    BootstrapperLog.Write("Workflow failed: " + ex);
                    _viewModel.SetFailure("Bootstrapper failed: " + ex.Message, true, true);
                }
            }
            finally
            {
                _running = false;
            }
        }

        private async void Retry_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
                return;
            await RunWorkflowAsync();
        }

        private async void LaunchCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsBusy || _closing)
                return;

            try
            {
                var result = await _launchCurrent();
                if (_closing)
                    return;
                if (result.Launched)
                {
                    Close();
                    return;
                }
                _viewModel.SetFailure(result.ErrorMessage ?? "Vellum could not start.", true, true);
            }
            catch (Exception ex)
            {
                if (!_closing)
                    _viewModel.SetFailure("Vellum could not start: " + ex.Message, true, true);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_closing)
                return;

            _display.Cancel();
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The window is already closing.
            }
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        protected override void OnClosed(EventArgs e)
        {
            _closing = true;
            BootstrapperLog.Write("Window closed.");
            _display.Cancel();
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already cancelled by the close button.
            }
            _cts.Dispose();
            base.OnClosed(e);
            _shutdown();
        }
    }
}
