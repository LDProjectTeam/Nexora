using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Nexora.Core.Helpers;
using Nexora.App.Services;
using Microsoft.Win32;

namespace Nexora.App.Views
{
    /// <summary>
    /// Диалог первого запуска: предлагает выбрать папку для библиотеки игр.
    /// По умолчанию показывает PathHelper.DefaultLibraryPath (%USERPROFILE%\Games\Nexora).
    /// </summary>
    public partial class FirstRunWindow : Window
    {
        /// <summary>Путь, который пользователь подтвердил.</summary>
        public string ChosenLibraryPath { get; private set; }

        public FirstRunWindow()
        {
            InitializeComponent();
            ChosenLibraryPath = PathHelper.DefaultLibraryPath;
            UpdatePathDisplay(ChosenLibraryPath);
        }

        private void UpdatePathDisplay(string path)
        {
            PathText.Text = path;

            // Show free disk space on the chosen drive
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
                var info = new DriveInfo(root);
                FreeSpaceText.Text = PathHelper.FormatBytes(info.AvailableFreeSpace);
                DiskInfoBorder.Background = info.AvailableFreeSpace > 10L * 1024 * 1024 * 1024
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(27, 58, 45))  // green-tint
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(58, 27, 27)); // red-tint (< 10 GB)
            }
            catch
            {
                FreeSpaceText.Text = "—";
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = LocalizationManager.GetString("FirstRunBrowseTitle"),
                InitialDirectory = Directory.Exists(ChosenLibraryPath)
                    ? ChosenLibraryPath
                    : @"C:\"
            };

            if (dialog.ShowDialog() == true)
            {
                ChosenLibraryPath = PathHelper.GetLibraryRoot(dialog.FolderName);
                UpdatePathDisplay(ChosenLibraryPath);
            }
        }

        private void UseDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            ChosenLibraryPath = PathHelper.DefaultLibraryPath;
            UpdatePathDisplay(ChosenLibraryPath);
            // Immediately confirm with the default path
            Confirm();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Confirm();

        private void Confirm()
        {
            DialogResult = true;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }
    }
}
