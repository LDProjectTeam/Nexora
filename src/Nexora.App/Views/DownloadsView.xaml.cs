using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nexora.App.ViewModels;

namespace Nexora.App.Views
{
    public partial class DownloadsView : UserControl
    {
        public DownloadsView()
        {
            InitializeComponent();
        }

        private void OnDownloadCardLoaded(object sender, RoutedEventArgs e)
        {
            UpdateDownloadCardClip(sender as Border);
        }

        private void OnDownloadCardSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateDownloadCardClip(sender as Border);
        }

        private static void UpdateDownloadCardClip(Border? card)
        {
            if (card == null || card.ActualWidth <= 0 || card.ActualHeight <= 0)
                return;

            const double cornerRadius = 14;
            card.Clip = new RectangleGeometry(
                new Rect(0, 0, card.ActualWidth, card.ActualHeight),
                cornerRadius,
                cornerRadius);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Any(f => f.EndsWith(".torrent", System.StringComparison.OrdinalIgnoreCase)))
                {
                    e.Effects = DragDropEffects.Copy;
                    DragOverlay.Visibility = Visibility.Visible;
                    e.Handled = true;
                    return;
                }
            }

            e.Effects = DragDropEffects.None;
            DragOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            base.OnDragLeave(e);
            DragOverlay.Visibility = Visibility.Collapsed;
        }

        private async void OnTorrentDrop(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null)
                {
                    var torrentFile = files.FirstOrDefault(f => f.EndsWith(".torrent", System.StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(torrentFile) && DataContext is MainViewModel vm)
                    {
                        await vm.InstallTorrentFileAsync(torrentFile);
                    }
                }
            }
        }
    }
}
