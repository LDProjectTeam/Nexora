using System;
using System.Linq;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;

using Nexora.App.ViewModels;
using Nexora.App.Views;
using Nexora.App.Services;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;

namespace Nexora.App
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ISettingsService _settingsService;
        private readonly IGameLauncher _gameLauncher;
        private WindowState _windowStateBeforeFullScreen;
        private bool _isFullScreen;
        private bool _isExiting;
        private bool _minimizedByGameLaunch;
        private bool _isShuttingDown;
        private WindowsTaskbarProgress? _taskbarProgress;
        private long _lastActivationTicks;

        public MainWindow(
            MainViewModel viewModel,
            ISettingsService settingsService,
            IGameLauncher gameLauncher)
        {
            InitializeComponent();
            TrayIcon.DoubleClickCommand = new RelayCommand(ShowFromTray);
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            _viewModel = viewModel;
            _settingsService = settingsService;
            _gameLauncher = gameLauncher;
            DataContext = _viewModel;
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.CurrentView))
                    AnimateCurrentPage();
                if (args.PropertyName is nameof(MainViewModel.TorrentDownloadProgressPercent)
                    or nameof(MainViewModel.HasActiveTorrentDownload)
                    or nameof(MainViewModel.ActiveDownloadsCount)
                    or nameof(MainViewModel.PausedDownloadsCount)
                    or nameof(MainViewModel.ErrorDownloadsCount))
                    UpdateTaskbarProgress();
            };

            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                _taskbarProgress = new WindowsTaskbarProgress(handle);
                System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WndProc);
                UpdateTaskbarProgress();
            };

            _gameLauncher.GameLaunched += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_settingsService.Settings.MinimizeOnGameLaunch)
                    {
                        _minimizedByGameLaunch = true;
                        MinimizeToTray();
                    }
                });
            };

            _gameLauncher.GameExited += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_minimizedByGameLaunch && _settingsService.Settings.RestoreOnGameExit)
                    {
                        _minimizedByGameLaunch = false;
                        ShowFromTray();
                    }
                });
            };

            StateChanged += (_, _) =>
            {
                MaxIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
                    ? "M9,5 H19 V15 H9 Z M5,9 V19 H15"
                    : "M5,5 H19 V19 H5 Z");
                RootWindow.BorderThickness = WindowState == WindowState.Maximized
                    ? new Thickness(6) : new Thickness(0);
            };

            Closing += async (s, e) =>
            {
                if (_isShuttingDown)
                    return;

                // Refuse to fully quit while a repack/installer is unpacking: killing the
                // launcher now would abort the install and corrupt the game files.
                // Minimizing to the tray is still allowed.
                var wantsFullExit = _isExiting || !_settingsService.Settings.MinimizeToTray;
                if (wantsFullExit && _viewModel.HasActiveInstallation)
                {
                    e.Cancel = true;
                    _isExiting = false;
                    TrayIcon.Visibility = Visibility.Visible;
                    if (!IsVisible)
                        Show();
                    Activate();
                    MessageBox.Show(
                        LocalizationManager.GetString("CloseBlockedInstallInProgress"),
                        LocalizationManager.GetString("CloseBlockedInstallTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (_settingsService.Settings.MinimizeToTray && !_isExiting)
                {
                    e.Cancel = true;
                    MinimizeToTray();
                    return;
                }

                e.Cancel = true;
                _isShuttingDown = true;
                TrayIcon.Visibility = Visibility.Collapsed;
                Hide();

                try
                {
                    await _viewModel.ShutdownAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Error("MainWindow", "Saving download state on exit failed.", ex);
                }

                Close();
            };

            Closed += (s, e) =>
            {
                _taskbarProgress?.Update(TaskbarProgressState.NoProgress);
                _taskbarProgress?.Dispose();
                _taskbarProgress = null;
                TrayIcon.Visibility = Visibility.Collapsed;
                Application.Current?.Shutdown();
            };

            Loaded += async (_, _) =>
            {
                try
                {
                    await _viewModel.InitializeAsync();

                    // Show first-run wizard if this is the first launch or library path is not set
                    if (_settingsService.IsFirstRun)
                    {
                        var wizard = new FirstRunWindow { Owner = this };
                        if (wizard.ShowDialog() == true)
                        {
                            _settingsService.Settings.DefaultInstallPath = wizard.ChosenLibraryPath;
                            PathHelper.EnsureDirectory(wizard.ChosenLibraryPath);
                            await _settingsService.SaveAsync();
                            _viewModel.SettingsVM.LoadSettings();
                        }
                    }
                }
                catch (Exception ex)
                {
                    ErrorReporter.Report(
                        "MainWindow.Loaded",
                        string.Format(
                            LocalizationManager.GetString("LoadFailedFormat", "Could not load the launcher data: {0}"),
                            ex.Message),
                        ex);
                }
            };
        }

        private void UpdateTaskbarProgress()
        {
            if (_taskbarProgress == null)
                return;

            var progress = _viewModel.GetTaskbarTorrentProgress();
            if (!progress.HasProgress)
            {
                _taskbarProgress.Update(TaskbarProgressState.NoProgress);
                return;
            }

            var state = progress.IsError
                ? TaskbarProgressState.Error
                : progress.IsPaused
                    ? TaskbarProgressState.Paused
                    : TaskbarProgressState.Normal;
            _taskbarProgress.Update(state, progress.Completed, progress.Total);
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService.Settings.MinimizeToTray)
                MinimizeToTray();
            else
                SystemCommands.MinimizeWindow(this);
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (_isFullScreen)
                ExitFullScreen();
            else if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();

        private void TrayOpen_Click(object sender, RoutedEventArgs e)
            => ShowFromTray();

        private void MinimizeToTray()
        {
            // No forced GC / working-set trim here: it caused a visible stall on restore
            // and the OS already trims pages of a background process under pressure.
            TrayIcon.Visibility = Visibility.Visible;
            Hide();
        }

        private void ShowFromTray()
        {
            _minimizedByGameLaunch = false;
            TrayIcon.Visibility = Visibility.Collapsed;
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>Обрабатывает запрос от второго экземпляра лаунчера: показать окно.</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == SingleInstanceSignal.ActivateMessage)
            {
                handled = true;

                // The second instance broadcasts several times to cover a slow startup;
                // handle only the first request in a short window.
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastActivationTicks) < 2000)
                    return IntPtr.Zero;

                Interlocked.Exchange(ref _lastActivationTicks, now);
                AppLog.Info("SingleInstance", "Activation requested by another launcher instance.");
                Dispatcher.BeginInvoke(new Action(ShowFromTray));
            }

            return IntPtr.Zero;
        }

        private const string TrayGameItemTag = "NexoraTrayGame";

        // Keep the tray menu compact, like Steam's recent games list.
        private const int TrayRecentGamesLimit = 5;

        private void TrayIcon_PreviewTrayContextMenuOpen(object sender, RoutedEventArgs e)
        {
            if (TrayIcon.ContextMenu is not { } menu)
                return;

            // Drop game entries from the previous open so the list stays current.
            for (var i = menu.Items.Count - 1; i >= 0; i--)
            {
                if (menu.Items[i] is System.Windows.Controls.MenuItem { Tag: TrayGameItemTag })
                    menu.Items.RemoveAt(i);
            }

            var installedGames = _viewModel.AllGameCards
                .Where(game => game.IsInstalled)
                .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(game => game.Installation.LastPlayedAt != default
                    ? game.Installation.LastPlayedAt
                    : game.Installation.InstalledAt)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(TrayRecentGamesLimit)
                .ToList();

            for (var i = 0; i < installedGames.Count; i++)
            {
                var game = installedGames[i];
                var gameMenuItem = new System.Windows.Controls.MenuItem
                {
                    Header = game.Name,
                    IsEnabled = game.PlayCommand.CanExecute(null),
                    Tag = TrayGameItemTag
                };
                gameMenuItem.Click += (_, _) => game.PlayCommand.Execute(null);
                menu.Items.Insert(i, gameMenuItem);
            }

            TrayGamesSeparator.Visibility = installedGames.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void TrayExit_Click(object sender, RoutedEventArgs e)
        {
            _isExiting = true;
            TrayIcon.Visibility = Visibility.Collapsed;
            Close();
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                e.Handled = true;
                if (_isFullScreen)
                    ExitFullScreen();
                else
                    EnterFullScreen();
            }
            else if (e.Key == Key.Escape && _isFullScreen)
            {
                e.Handled = true;
                ExitFullScreen();
            }
        }

        private void EnterFullScreen()
        {
            if (_isFullScreen)
                return;

            _windowStateBeforeFullScreen = WindowState;
            _isFullScreen = true;
            WindowControlButtons.Visibility = Visibility.Collapsed;
            if (WindowState != WindowState.Maximized)
                SystemCommands.MaximizeWindow(this);
        }

        private void ExitFullScreen()
        {
            if (!_isFullScreen)
                return;

            _isFullScreen = false;
            WindowControlButtons.Visibility = Visibility.Visible;
            if (_windowStateBeforeFullScreen == WindowState.Normal
                && WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(this);
            }
        }



        private void WindowDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (WindowState == WindowState.Maximized)
                    SystemCommands.RestoreWindow(this);
                else
                    SystemCommands.MaximizeWindow(this);
            }
            else if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void AnimateCurrentPage()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var page = _viewModel.CurrentView switch
                {
                    "Library" => LibraryPage,
                    "Downloads" => DownloadsPage,
                    "Settings" => SettingsPage,
                    _ => HomePage
                };

                page.BeginAnimation(OpacityProperty, null);
                page.Opacity = 0;
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                page.BeginAnimation(OpacityProperty, fade);

                if (page.RenderTransform is TranslateTransform translate)
                {
                    translate.BeginAnimation(TranslateTransform.XProperty, null);
                    translate.X = 10;
                    var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    translate.BeginAnimation(TranslateTransform.XProperty, slide);
                }
            }));
        }
    }
}
