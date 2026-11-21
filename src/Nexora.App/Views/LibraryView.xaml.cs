using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Nexora.App.ViewModels;

namespace Nexora.App.Views
{
    public partial class LibraryView : UserControl
    {
        public static readonly DependencyProperty LibraryCardWidthProperty = DependencyProperty.Register(
            nameof(LibraryCardWidth), typeof(double), typeof(LibraryView), new PropertyMetadata(230d));

        public double LibraryCardWidth => (double)GetValue(LibraryCardWidthProperty);

        private readonly DispatcherTimer _visibleArtworkTimer;
        private MainViewModel? _viewModel;
        private DateTime _lastArtworkScrollUtc;
        private DateTime _lastArtworkPrefetchUtc;

        public LibraryView()
        {
            InitializeComponent();
            _visibleArtworkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _visibleArtworkTimer.Tick += (_, _) => UpdateVisibleArtwork();
            DataContextChanged += LibraryView_DataContextChanged;
            Loaded += (_, _) =>
            {
                UpdateLibraryCardWidth();
                ScheduleVisibleArtworkUpdate();
            };
            Unloaded += (_, _) => _visibleArtworkTimer.Stop();
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                {
                    UpdateLibraryCardWidth();
                    ScheduleVisibleArtworkUpdate();
                }
            };
            SizeChanged += (_, _) =>
            {
                UpdateLibraryCardWidth();
                ScheduleVisibleArtworkUpdate();
            };
        }

        /// <summary>
        /// Stretches the cards to fill whole columns instead of leaving a ragged
        /// empty strip after the last card when the window is resized.
        /// </summary>
        private void UpdateLibraryCardWidth()
        {
            const double cardGap = 6;      // 3 px on each side of a card
            const double minCardWidth = 218;
            const double maxCardWidth = 250;

            var available = ActualWidth - 72; // page padding + scrollbar
            if (available <= 0)
                return;

            var columns = Math.Max(2, (int)Math.Floor((available + cardGap) / (minCardWidth + cardGap)));
            var cardWidth = Math.Clamp((available - columns * cardGap) / columns, minCardWidth, maxCardWidth);
            SetValue(LibraryCardWidthProperty, cardWidth);
        }

        private void LibraryView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.LibraryGames.CollectionChanged -= LibraryGames_CollectionChanged;

            _viewModel = DataContext as MainViewModel;
            if (_viewModel != null)
                _viewModel.LibraryGames.CollectionChanged += LibraryGames_CollectionChanged;

            ScheduleVisibleArtworkUpdate();
        }

        private void LibraryGames_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => ScheduleVisibleArtworkUpdate();

        private void LibraryScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
            => ScheduleVisibleArtworkUpdate();

        private void ScheduleVisibleArtworkUpdate()
        {
            _lastArtworkScrollUtc = DateTime.UtcNow;
            if (!_visibleArtworkTimer.IsEnabled)
                _visibleArtworkTimer.Start();
        }

        private void UpdateVisibleArtwork()
        {
            if (!IsLoaded || _viewModel == null)
            {
                _visibleArtworkTimer.Stop();
                return;
            }

            var visibleCards = new List<GameCardViewModel>();
            var nearbyCards = new List<(double Distance, GameCardControl Card)>();
            CollectArtworkCards(LibraryScrollViewer, LibraryScrollViewer, visibleCards, nearbyCards);
            _viewModel.SetVisibleArtworkCards(visibleCards);
            StartArtworkPrefetch(nearbyCards);

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

        private void Library_PreviewDragOver(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            var canDrop = paths?.Any(path => string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) == true;
            e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
            LibraryDropOverlay.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void Library_PreviewDragLeave(object sender, DragEventArgs e)
            => LibraryDropOverlay.Visibility = Visibility.Collapsed;

        private async void Library_PreviewDrop(object sender, DragEventArgs e)
        {
            LibraryDropOverlay.Visibility = Visibility.Collapsed;

            if (DataContext is not MainViewModel viewModel || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
                return;

            e.Handled = true;
            foreach (var path in paths.Where(path => string.Equals(
                         Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)))
                await viewModel.ImportShortcutAsync(path);
        }
    }
}
