using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vellum.App.Views
{
    /// <summary>
    /// One download task card. Extracted from DownloadsView so the page keeps only
    /// the header/filters and the list, while the card owns its clipping logic.
    /// </summary>
    public partial class DownloadsTaskCard : UserControl
    {
        public DownloadsTaskCard()
        {
            InitializeComponent();
        }

        private void OnDownloadCardLoaded(object sender, RoutedEventArgs e)
            => UpdateDownloadCardClip(sender as Border);

        private void OnDownloadCardSizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateDownloadCardClip(sender as Border);

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
    }
}
