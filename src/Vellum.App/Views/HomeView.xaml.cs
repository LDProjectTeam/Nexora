using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vellum.App.Converters;
using Vellum.App.ViewModels;
using Vellum.App.Services;

namespace Vellum.App.Views
{
    public partial class HomeView : UserControl
    {
        public static readonly DependencyProperty CatalogCardWidthProperty = DependencyProperty.Register(
            nameof(CatalogCardWidth), typeof(double), typeof(HomeView), new PropertyMetadata(318d));

        public double CatalogCardWidth => (double)GetValue(CatalogCardWidthProperty);

        private readonly DispatcherTimer _artworkWaitTimer;
        private readonly DispatcherTimer _visibleArtworkTimer;
        private ScrollViewer? _pendingArtworkViewport;
        private MainViewModel? _viewModel;
        private DateTime _artworkWaitDeadline;
        private DateTime _artworkGraceDeadline;
        private DateTime _lastArtworkScrollUtc;
        private DateTime _lastArtworkPrefetchUtc;
        private int _transitionVersion;
        private int _surpriseRollVersion;
        private bool _transitionPending;
        private bool _pendingCaptionSwap;
        private ImageSource? _displayedArtworkSource;
        private string? _displayedArtworkKey;

        public HomeView()
        {
            InitializeComponent();
            _artworkWaitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _artworkWaitTimer.Tick += (_, _) => TryBeginFeaturedTransition();
            _visibleArtworkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _visibleArtworkTimer.Tick += VisibleArtworkTimer_Tick;
            DataContextChanged += HomeView_DataContextChanged;
            SizeChanged += (_, _) =>
            {
                UpdateCatalogCardWidth();
                ScheduleVisibleArtworkUpdate(CatalogScrollViewer);
            };
            Loaded += (_, _) =>
            {
                UpdateCatalogCardWidth();
                ScheduleVisibleArtworkUpdate(CatalogScrollViewer);
            };
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                    ScheduleVisibleArtworkUpdate(CatalogScrollViewer);
            };
            Unloaded += (_, _) => _visibleArtworkTimer.Stop();
        }

        private void HomeView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.FeaturedGameChanging -= ViewModel_FeaturedGameChanging;
            }

            _viewModel = DataContext as MainViewModel;
            UpdateFeaturedText(_viewModel?.FeaturedGame);
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                _viewModel.FeaturedGameChanging += ViewModel_FeaturedGameChanging;
            }
        }

        private void ViewModel_FeaturedGameChanging(GameCardViewModel? oldGame, GameCardViewModel? newGame)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ViewModel_FeaturedGameChanging(oldGame, newGame));
                return;
            }

            // The binding has not switched to the new slide yet, so the element
            // that is on top still holds the outgoing artwork.
            var keepCurrentArtwork = CurrentFeaturedArtwork.Opacity >= PreviousFeaturedArtwork.Opacity;
            var outgoingImage = keepCurrentArtwork ? FeaturedHeroImage : PreviousHeroImage;
            BeginArtworkSwap(outgoingImage, sourceOverride: null, swapCaption: true);

            // Keep the outgoing caption on screen while the new artwork loads.
            // The caption and the artwork are swapped together in
            // TryBeginFeaturedTransition, so the hero never shows the fallback
            // backdrop (or a new title over the previous game's art).
            // Restoring the opacity also covers repeated slide changes that
            // interrupt a caption fade and would otherwise leave it invisible.
            FeaturedContent.BeginAnimation(OpacityProperty, null);
            FeaturedContentTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            FeaturedContent.Opacity = 1;
            FeaturedContentTranslation.Y = 0;
        }

        /// <summary>
        /// Keeps the outgoing artwork on screen and starts the timer that swaps in
        /// the next one once its bitmap is decoded.
        /// </summary>
        private void BeginArtworkSwap(Image outgoingImage, ImageSource? sourceOverride, bool swapCaption)
        {
            _artworkWaitTimer.Stop();
            _transitionVersion++;
            _transitionPending = true;
            _pendingCaptionSwap = swapCaption;
            PreviousCaptionSnapshot.BeginAnimation(OpacityProperty, null);
            PreviousCaptionSnapshot.Opacity = 0;
            PreviousCaptionSnapshot.Source = null;
            // While the game has an artwork URL the slide waits for the bitmap to
            // decode (hard cap below), otherwise give the artwork lookup a short
            // grace period before revealing the designed backdrop.
            _artworkWaitDeadline = DateTime.UtcNow.AddSeconds(10);
            _artworkGraceDeadline = DateTime.UtcNow.AddSeconds(2.5);

            var outgoingSource = sourceOverride ?? outgoingImage.Source;
            PreviousHeroImage.Source = outgoingSource;
            PreviousHeroImage.Stretch = outgoingImage.Stretch;
            PreviousHeroImage.Width = outgoingImage.Width;
            PreviousHeroImage.Height = outgoingImage.Height;
            PreviousHeroImage.Opacity = outgoingImage.Opacity;
            PreviousHeroImage.HorizontalAlignment = outgoingImage.HorizontalAlignment;
            PreviousHeroImage.VerticalAlignment = outgoingImage.VerticalAlignment;
            PreviousHeroImage.Margin = outgoingImage.Margin;

            PreviousFeaturedArtwork.BeginAnimation(OpacityProperty, null);
            CurrentFeaturedArtwork.BeginAnimation(OpacityProperty, null);
            PreviousFeaturedArtwork.Opacity = outgoingSource != null ? 1 : 0;
            CurrentFeaturedArtwork.Opacity = 0;

            if (!_artworkWaitTimer.IsEnabled)
                _artworkWaitTimer.Start();
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsSurpriseCaseOpen))
            {
                if (_viewModel?.IsSurpriseCaseOpen == true)
                    StartSurpriseCaseAnimation();
                return;
            }

            if (e.PropertyName != nameof(MainViewModel.FeaturedGame) || !_transitionPending)
                return;

            var transitionVersion = _transitionVersion;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (transitionVersion == _transitionVersion)
                    TryBeginFeaturedTransition();
            }));
        }

        private void TryBeginFeaturedTransition()
        {
            if (!_transitionPending)
                return;

            var newGame = _viewModel?.FeaturedGame;
            var newSource = FeaturedHeroImage.Source;
            var expectedArtwork = GetArtworkKey(newGame?.FeaturedImageUrl);
            var artworkReady = expectedArtwork != null
                && IsArtworkSource(newSource, expectedArtwork)
                && !IsImageDownloading(newSource);
            var hasPreviousArtwork = PreviousHeroImage.Source != null;
            var hasArtworkUrl = !string.IsNullOrWhiteSpace(newGame?.FeaturedImageUrl);

            if (!artworkReady && hasPreviousArtwork)
            {
                // Hold the outgoing slide instead of flashing the empty backdrop.
                // A pending URL may still turn into a decoded bitmap, so keep
                // polling for as long as the artwork is expected to arrive.
                var stillWaitingForArtwork = hasArtworkUrl
                    ? DateTime.UtcNow < _artworkWaitDeadline
                    : DateTime.UtcNow < _artworkGraceDeadline;
                if (stillWaitingForArtwork)
                {
                    if (!_artworkWaitTimer.IsEnabled)
                        _artworkWaitTimer.Start();
                    return;
                }
            }
            else if (!artworkReady && !hasPreviousArtwork && hasArtworkUrl
                     && DateTime.UtcNow < _artworkWaitDeadline)
            {
                // Nothing to hold on to yet: wait for the first slide to decode.
                if (!_artworkWaitTimer.IsEnabled)
                    _artworkWaitTimer.Start();
                return;
            }

            _artworkWaitTimer.Stop();
            _transitionPending = false;
            _displayedArtworkSource = artworkReady ? newSource : null;
            _displayedArtworkKey = artworkReady ? expectedArtwork : null;
            var transitionVersion = _transitionVersion;
            var duration = TimeSpan.FromMilliseconds(420);
            var previousHasImage = PreviousHeroImage.Source != null;

            if (artworkReady)
            {
                CurrentFeaturedArtwork.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            else
            {
                // No usable artwork for this game: reveal the designed backdrop.
                CurrentFeaturedArtwork.BeginAnimation(OpacityProperty, null);
                CurrentFeaturedArtwork.Opacity = 0;
            }

            if (previousHasImage)
            {
                var fadeOut = new DoubleAnimation(1, 0, artworkReady ? duration : TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                fadeOut.Completed += (_, _) =>
                {
                    if (transitionVersion == _transitionVersion)
                    {
                        PreviousHeroImage.Source = null;
                    }
                };
                PreviousFeaturedArtwork.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                PreviousFeaturedArtwork.Opacity = 0;
            }

            if (_pendingCaptionSwap)
                BeginFeaturedContentSwap(newGame, transitionVersion);
            _pendingCaptionSwap = false;
        }

        private void BeginFeaturedContentSwap(GameCardViewModel? game, int transitionVersion)
        {
            if (transitionVersion != _transitionVersion)
                return;

            FeaturedContent.BeginAnimation(OpacityProperty, null);
            FeaturedContentTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            FeaturedContent.Opacity = 1;
            FeaturedContentTranslation.Y = 0;
            FeaturedContent.UpdateLayout();

            // Freeze the outgoing caption and dissolve it over the new one, so
            // the text is never dimmed or blank during a slide change.
            var snapshot = CaptureFeaturedCaption();
            UpdateFeaturedText(game);
            FeaturedContent.UpdateLayout();

            if (snapshot != null)
                CrossfadeFeaturedCaption(snapshot);
        }

        private ImageSource? CaptureFeaturedCaption()
        {
            var width = FeaturedContent.ActualWidth;
            var height = FeaturedContent.ActualHeight;
            if (width <= 0 || height <= 0)
                return null;

            try
            {
                var dpi = VisualTreeHelper.GetDpi(FeaturedContent);
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(width * dpi.DpiScaleX),
                    (int)Math.Ceiling(height * dpi.DpiScaleY),
                    dpi.PixelsPerInchX,
                    dpi.PixelsPerInchY,
                    PixelFormats.Pbgra32);
                bitmap.Render(FeaturedContent);
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void CrossfadeFeaturedCaption(ImageSource snapshot)
        {
            var origin = FeaturedContent.TranslatePoint(new Point(0, 0), HeroRoot);
            PreviousCaptionSnapshot.Margin = new Thickness(origin.X, origin.Y, 0, 0);
            PreviousCaptionSnapshot.Width = FeaturedContent.ActualWidth;
            PreviousCaptionSnapshot.Height = FeaturedContent.ActualHeight;
            PreviousCaptionSnapshot.Source = snapshot;
            PreviousCaptionSnapshot.BeginAnimation(OpacityProperty, null);
            PreviousCaptionSnapshot.Opacity = 1;

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += (_, _) =>
            {
                if (!ReferenceEquals(PreviousCaptionSnapshot.Source, snapshot))
                    return;

                PreviousCaptionSnapshot.BeginAnimation(OpacityProperty, null);
                PreviousCaptionSnapshot.Opacity = 0;
                PreviousCaptionSnapshot.Source = null;
            };
            PreviousCaptionSnapshot.BeginAnimation(OpacityProperty, fade);
        }

        private void UpdateFeaturedText(GameCardViewModel? game)
        {
            FeaturedTitleText.Text = game?.Name ?? LocalizationManager.GetString("FeaturedFallbackTitle");
            FeaturedDescriptionText.Text = game?.Description
                ?? LocalizationManager.GetString("FeaturedFallbackSubtitle");
        }

        private void StartSurpriseCaseAnimation()
        {
            var version = ++_surpriseRollVersion;
            SurpriseCaseStripTransform.BeginAnimation(TranslateTransform.XProperty, null);
            SurpriseCaseStripTransform.X = 0;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (version != _surpriseRollVersion || _viewModel?.SurpriseWinner == null)
                    return;

                SurpriseCaseItemsControl.UpdateLayout();
                const double slotWidth = 200;
                var winningIndex = _viewModel.SurpriseRollItems.IndexOf(_viewModel.SurpriseWinner);
                if (winningIndex < 0)
                    return;
                var targetOffset = Math.Max(0,
                    (winningIndex + 0.5) * slotWidth - SurpriseCaseViewport.ActualWidth / 2);
                var animation = new DoubleAnimation(0, -targetOffset, TimeSpan.FromSeconds(5.2))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    DecelerationRatio = 0.82
                };
                animation.Completed += (_, _) =>
                {
                    if (version == _surpriseRollVersion)
                        CompleteSurpriseCaseReveal();
                };
                SurpriseCaseStripTransform.BeginAnimation(TranslateTransform.XProperty, animation);
            }));
        }

        private void CompleteSurpriseCaseReveal()
        {
            if (_viewModel?.SurpriseWinner is not GameCardViewModel winner)
            {
                CloseSurpriseCase();
                return;
            }

            _surpriseRollVersion++;
            SurpriseCaseStripTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _viewModel.FinishSurpriseCase();
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => winner.OpenDetailsCommand.Execute(null)));
        }

        private void CloseSurpriseCase()
        {
            _surpriseRollVersion++;
            SurpriseCaseStripTransform.BeginAnimation(TranslateTransform.XProperty, null);
            if (_viewModel?.IsSurpriseCaseOpen == true)
                _viewModel.CancelSurpriseCase();
        }

        private void SurpriseCaseClose_Click(object sender, RoutedEventArgs e)
            => CloseSurpriseCase();

        private void SurpriseCaseSkip_Click(object sender, RoutedEventArgs e)
            => CompleteSurpriseCaseReveal();

        private void FeaturedHeroImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
            => _viewModel?.FeaturedGame?.UseCoverAsFeaturedFallback();

        private void FeaturedHeroImage_TargetUpdated(object? sender, DataTransferEventArgs e)
        {
            if (_transitionPending)
            {
                var transitionVersion = _transitionVersion;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    if (transitionVersion == _transitionVersion)
                        TryBeginFeaturedTransition();
                }));
                return;
            }

            // The same slide received another artwork URL (for example a Steam
            // banner replaced the cached cover while the catalogue was still
            // loading). Crossfade it: swapping the Image.Source directly makes
            // the hero blink while the new bitmap decodes or fails to download.
            var expectedArtwork = GetArtworkKey(_viewModel?.FeaturedGame?.FeaturedImageUrl);
            if (expectedArtwork == null
                || string.Equals(expectedArtwork, _displayedArtworkKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            BeginArtworkSwap(FeaturedHeroImage, _displayedArtworkSource, swapCaption: false);
        }

        private static bool IsImageDownloading(ImageSource? source)
            => source is BitmapImage bitmap && bitmap.IsDownloading;

        private static bool IsArtworkSource(ImageSource? source, string expectedKey)
            => StringToImageSourceConverter.MatchesSourceKey(source, expectedKey);

        /// <summary>
        /// Normalizes the featured artwork URL the same way the XAML converter
        /// builds its <see cref="BitmapImage"/>, so both can be compared.
        /// </summary>
        private static string? GetArtworkKey(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
                return absolute.AbsoluteUri;

            try
            {
                var path = url;
                if (!System.IO.File.Exists(path))
                {
                    var baseRelative = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, path);
                    if (System.IO.File.Exists(baseRelative))
                        path = baseRelative;
                }

                if (!System.IO.Path.IsPathRooted(path))
                    path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

                return new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateCatalogCardWidth()
        {
            const double originalCardWidth = 318;
            const double fiveColumnBreakpoint = 1200;
            var cardWidth = ActualWidth < fiveColumnBreakpoint
                ? originalCardWidth
                : Math.Clamp((ActualWidth - 40) / 5 - 7, 220, originalCardWidth);
            SetValue(CatalogCardWidthProperty, cardWidth);
        }

        private void CatalogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer || DataContext is not MainViewModel viewModel)
                return;

            ScheduleVisibleArtworkUpdate(scrollViewer);

            if (scrollViewer.VerticalOffset + scrollViewer.ViewportHeight >= scrollViewer.ExtentHeight - 600)
            {
                viewModel.LoadMoreDisplayedCatalogGames();
                if (viewModel.LoadMoreCatalogGamesCommand.CanExecute(null))
                    viewModel.LoadMoreCatalogGamesCommand.Execute(null);
            }
        }

        private void ScheduleVisibleArtworkUpdate(ScrollViewer viewport)
        {
            _pendingArtworkViewport = viewport;
            _lastArtworkScrollUtc = DateTime.UtcNow;
            // Keep the timer running while the list is moving instead of restarting it
            // on every scroll event: the cards under the viewport then start loading
            // during the scroll, not only after it stops.
            if (!_visibleArtworkTimer.IsEnabled)
                _visibleArtworkTimer.Start();
        }

        private void VisibleArtworkTimer_Tick(object? sender, EventArgs e)
        {
            var viewport = _pendingArtworkViewport;
            if (viewport == null || DataContext is not MainViewModel viewModel || !IsLoaded)
            {
                _visibleArtworkTimer.Stop();
                return;
            }

            var visibleCards = new List<GameCardViewModel>();
            var nearbyCards = new List<(double Distance, GameCardControl Card)>();
            CollectArtworkCards(viewport, viewport, visibleCards, nearbyCards);
            viewModel.SetVisibleArtworkCards(visibleCards);
            StartArtworkPrefetch(nearbyCards);

            // Stop only after the scroll settled and the lookahead is prepared.
            if (nearbyCards.Count == 0 &&
                DateTime.UtcNow - _lastArtworkScrollUtc > TimeSpan.FromMilliseconds(200))
            {
                _visibleArtworkTimer.Stop();
            }
        }

        private void StartArtworkPrefetch(List<(double Distance, GameCardControl Card)> nearbyCards)
        {
            if (nearbyCards.Count == 0)
                return;

            var now = DateTime.UtcNow;
            if (now - _lastArtworkPrefetchUtc < TimeSpan.FromMilliseconds(220))
                return;

            _lastArtworkPrefetchUtc = now;
            // Nearest cards first, in small batches so the prefetch never competes
            // with what is on screen right now.
            foreach (var entry in nearbyCards.OrderBy(item => item.Distance).Take(6))
                entry.Card.StartArtworkPrefetch();
        }

        private static void CollectArtworkCards(
            DependencyObject current,
            ScrollViewer viewport,
            List<GameCardViewModel> visibleCards,
            List<(double Distance, GameCardControl Card)> nearbyCards)
        {
            if (current is GameCardControl card)
            {
                if (card.TryLoadArtworkIfVisible(viewport))
                {
                    if (card.DataContext is GameCardViewModel visibleGame)
                        visibleCards.Add(visibleGame);
                    return;
                }

                if (card.TryGetArtworkPrefetchDistance(viewport, out var distance))
                    nearbyCards.Add((distance, card));
                return;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++)
                CollectArtworkCards(VisualTreeHelper.GetChild(current, index), viewport, visibleCards, nearbyCards);
        }
    }
}
