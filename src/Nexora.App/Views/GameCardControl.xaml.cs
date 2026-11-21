using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using Nexora.App.ViewModels;

namespace Nexora.App.Views
{
    public partial class GameCardControl : UserControl
    {
        public static readonly DependencyProperty ArtworkHeightProperty = DependencyProperty.Register(
            nameof(ArtworkHeight), typeof(double), typeof(GameCardControl), new PropertyMetadata(180d));

        private readonly DispatcherTimer _artworkRetryTimer;
        private int _retryIntervalSeconds = 2;
        private bool _prefetchStarted;

        public double ArtworkHeight
        {
            get => (double)GetValue(ArtworkHeightProperty);
            set => SetValue(ArtworkHeightProperty, value);
        }

        public GameCardControl()
        {
            InitializeComponent();
            _artworkRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _artworkRetryTimer.Tick += ArtworkRetryTimer_Tick;
            Loaded += GameCardControl_Loaded;
            Unloaded += GameCardControl_Unloaded;
        }

        private void GameCardControl_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureCardWidth();
            TryLoadArtworkIfVisible();
        }

        /// <summary>
        /// The catalog supplies the card width through an ancestor binding. If a card is
        /// regenerated outside the visual tree that binding can fail and leave the card
        /// collapsed to the width of its buttons, so restore a sane width on load.
        /// </summary>
        private void EnsureCardWidth()
        {
            if (!double.IsNaN(Width) && Width > 0)
                return;

            DependencyObject? current = VisualTreeHelper.GetParent(this);
            while (current != null)
            {
                if (current is HomeView home)
                {
                    Width = home.CatalogCardWidth;
                    return;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            Width = 318;
        }

        private void GameCardControl_Unloaded(object sender, RoutedEventArgs e)
            => _artworkRetryTimer.Stop();

        private async void ArtworkRetryTimer_Tick(object? sender, EventArgs e)
        {
            if (DataContext is not GameCardViewModel game)
            {
                _artworkRetryTimer.Stop();
                return;
            }

            if (!TryGetViewport(out var viewport) ||
                !TryGetBounds(viewport, out var bounds) ||
                !IntersectsVisibleBand(viewport, bounds))
            {
                _artworkRetryTimer.Stop();
                return;
            }

            if (HasCompleteArtwork(game))
            {
                _artworkRetryTimer.Stop();
                return;
            }

            await TryLoadArtworkAsync(game);

            if (HasCompleteArtwork(game))
            {
                _artworkRetryTimer.Stop();
            }
            else
            {
                _retryIntervalSeconds = Math.Min(_retryIntervalSeconds * 2, 15);
                _artworkRetryTimer.Interval = TimeSpan.FromSeconds(_retryIntervalSeconds);
            }
        }

        private static bool HasCompleteArtwork(GameCardViewModel game)
            => game.HasCoverArtwork;

        private static async Task TryLoadArtworkAsync(GameCardViewModel game)
        {
            try { await game.EnsureArtworkLoadedAsync(priority: Nexora.Core.Interfaces.ArtworkPriority.Visible); }
            catch { /* Artwork is optional; the card keeps its placeholder and retries while visible. */ }
        }

        public bool TryLoadArtworkIfVisible(ScrollViewer? viewport = null)
        {
            if (DataContext is not GameCardViewModel game)
                return false;

            // Most cards already carry a cover: leave them untouched and cheap.
            if (HasCompleteArtwork(game))
            {
                _prefetchStarted = true;
                _artworkRetryTimer.Stop();
                return false;
            }

            if (!IsLoaded)
                return false;

            if (viewport == null && !TryGetViewport(out viewport))
                return false;

            if (!TryGetBounds(viewport, out var bounds) || !IntersectsVisibleBand(viewport, bounds))
            {
                _artworkRetryTimer.Stop();
                return false;
            }

            _prefetchStarted = true;
            _retryIntervalSeconds = 2;
            _artworkRetryTimer.Interval = TimeSpan.FromSeconds(2);
            if (!_artworkRetryTimer.IsEnabled)
                _artworkRetryTimer.Start();

            _ = TryLoadArtworkAsync(game);
            return true;
        }

        /// <summary>
        /// Returns how far below the viewport this card sits, when it is close enough
        /// to be resolved ahead of time.
        /// </summary>
        public bool TryGetArtworkPrefetchDistance(ScrollViewer viewport, out double distance)
        {
            distance = 0;
            if (_prefetchStarted || !IsLoaded || DataContext is not GameCardViewModel game)
                return false;

            if (HasCompleteArtwork(game))
            {
                _prefetchStarted = true;
                return false;
            }

            if (!TryGetBounds(viewport, out var bounds))
                return false;

            // Cards inside the load band are handled by the visible path.
            if (IntersectsVisibleBand(viewport, bounds))
                return false;

            var lookahead = Math.Max(900, viewport.ViewportHeight * 2.2);
            if (bounds.Bottom < 0 || bounds.Top > viewport.ViewportHeight + lookahead)
                return false;

            distance = Math.Max(0, bounds.Top - viewport.ViewportHeight);
            return true;
        }

        /// <summary>Starts a low-priority lookup so the cover is ready before it scrolls into view.</summary>
        public bool StartArtworkPrefetch()
        {
            if (_prefetchStarted || DataContext is not GameCardViewModel game || HasCompleteArtwork(game))
                return false;

            _prefetchStarted = true;
            _ = game.EnsureArtworkLoadedAsync(priority: Nexora.Core.Interfaces.ArtworkPriority.Background);
            return true;
        }

        private bool TryGetViewport(out ScrollViewer viewport)
        {
            DependencyObject? current = this;
            while (current != null)
            {
                if (current is ScrollViewer scrollViewer)
                {
                    viewport = scrollViewer;
                    return true;
                }

                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            viewport = null!;
            return false;
        }

        private bool TryGetBounds(ScrollViewer viewport, out Rect bounds)
        {
            try
            {
                bounds = TransformToAncestor(viewport).TransformBounds(
                    new Rect(new Point(0, 0), RenderSize));
                return bounds.Width > 0 && bounds.Height > 0;
            }
            catch (InvalidOperationException)
            {
                bounds = Rect.Empty;
                return false;
            }
            catch (ArgumentException)
            {
                bounds = Rect.Empty;
                return false;
            }
        }

        private static bool IntersectsVisibleBand(ScrollViewer viewport, Rect bounds)
        {
            // Load a little above and almost a full screen below: the next rows are
            // resolved before the user scrolls to them.
            const double above = 320;
            var below = Math.Max(320, viewport.ViewportHeight * 0.9);
            var band = new Rect(
                0,
                -above,
                viewport.ViewportWidth,
                viewport.ViewportHeight + above + below);
            return bounds.IntersectsWith(band);
        }

        private void CardBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Border border)
            {
                var radius = border.CornerRadius.TopLeft;
                border.Clip = new RectangleGeometry(
                    new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                    radius,
                    radius);
            }
        }

        private void Card_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject))
                return;

            if (DataContext is GameCardViewModel game)
                game.OpenDetailsCommand.Execute(null);

            e.Handled = true;
        }

        private static bool IsInsideButton(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is ButtonBase)
                    return true;
                element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }

            return false;
        }
    }
}
