using System;
using System.Windows;
using System.Windows.Input;
using Vellum.App.ViewModels;
using Vellum.Core.Interfaces;

namespace Vellum.App.Views
{
    public partial class GameDetailsWindow : Window
    {
        private readonly GameDetailsViewModel _viewModel;
        private bool _disposed;

        public GameDetailsWindow(
            GameCardViewModel game,
            IGameSearchAggregator searchAggregator,
            string installPath,
            ISteamMetadataService? steamMetadataService = null)
        {
            InitializeComponent();

            _viewModel = new GameDetailsViewModel(game, searchAggregator, installPath, steamMetadataService);
            DataContext = _viewModel;

            _viewModel.CloseRequested += OnCloseRequested;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        public GameDetailsViewModel ViewModel => _viewModel;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Keep the window usable on small screens and at high DPI: the default
            // size is a preference, not a hard requirement.
            var workArea = SystemParameters.WorkArea;
            var maxWidth = Math.Max(720, workArea.Width - 48);
            var maxHeight = Math.Max(560, workArea.Height - 48);

            if (Width > maxWidth)
                Width = maxWidth;
            if (Height > maxHeight)
                Height = maxHeight;

            if (MinWidth > maxWidth)
                MinWidth = maxWidth;
            if (MinHeight > maxHeight)
                MinHeight = maxHeight;

            if (WindowStartupLocation == WindowStartupLocation.CenterOwner && Owner == null)
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void OnCloseRequested(object? sender, EventArgs e) => Close();

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_disposed)
                return;

            _disposed = true;
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel.Dispose();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
