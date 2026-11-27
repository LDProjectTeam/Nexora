using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Vellum.Core.Models;

namespace Vellum.Bootstrapper
{
    public partial class BootstrapperWindow : Window
    {
        private readonly BootstrapperViewModel _viewModel = new();
        private readonly Func<IProgress<BootstrapperProgress>, CancellationToken, Task<BootstrapperRunResult>> _run;
        private readonly Func<Task<BootstrapperRunResult>> _launchCurrent;
        private readonly Action _shutdown;
        private CancellationTokenSource? _cts;
        private bool _running;
        private bool _acceptProgress;

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
            await RunWorkflowAsync();
        }

        private async Task RunWorkflowAsync()
        {
            if (_running)
                return;

            _running = true;
            _cts = new CancellationTokenSource();
            _acceptProgress = true;
            var progress = new Progress<BootstrapperProgress>(p =>
            {
                if (_acceptProgress)
                    _viewModel.Apply(p);
            });
            try
            {
                var result = await _run(progress, _cts.Token);
                _acceptProgress = false;
                BootstrapperLog.Write(
                    $"Workflow result: success={result.Success}, launched={result.Launched}, " +
                    $"canLaunch={result.CanLaunchCurrent}, error={result.ErrorMessage}");
                if (result.Success)
                {
                    _viewModel.SetStarting();
                    Close();
                    return;
                }

                _viewModel.SetFailure(result.ErrorMessage ?? "Unknown error",
                    result.CanRetry, result.CanLaunchCurrent);
            }
            catch (OperationCanceledException)
            {
                _acceptProgress = false;
                if (IsVisible)
                    _viewModel.SetFailure("Cancelled.", true, false);
            }
            catch (Exception ex)
            {
                _acceptProgress = false;
                _viewModel.SetFailure("Bootstrapper failed: " + ex.Message, true, true);
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
            if (_viewModel.IsBusy)
                return;
            var result = await _launchCurrent();
            if (result.Launched)
            {
                Close();
                return;
            }
            _viewModel.SetFailure(result.ErrorMessage ?? "Vellum could not start.", true, true);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Dispose();
            _cts = null;
            base.OnClosed(e);
            _shutdown();
        }
    }
}
