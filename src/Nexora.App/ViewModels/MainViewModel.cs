using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Services;
using Nexora.App.Services;
using Nexora.Core.Models;

namespace Nexora.App.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly IGameCatalog _catalog;
        private readonly IGameManager _gameManager;
        private readonly IGameLauncher _gameLauncher;
        private readonly ISettingsService _settingsService;
        private readonly IGameSearchAggregator _searchAggregator;
        private readonly IGameArtworkService _artworkService;
        private readonly ISteamFeaturedGamesService _steamFeaturedGamesService;
        private readonly NotificationService _notifications;
        private CancellationTokenSource? _featuredGamesLoadCts;
        private List<GameCardViewModel>? _steamFeaturedGames;
        private GameBrowseOrder? _activeBrowseOrder;
        private int _nextBrowsePage;
        private int _browseGeneration;
        private bool _hasMoreBrowsePages;
        private bool _isLoadingMoreGames;
        private readonly HashSet<string> _builtInTitleKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _rutorTitleKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _onlineSearchResultTitleKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GameCardViewModel> _preferredReleaseCards = new(StringComparer.Ordinal);
        private readonly List<GameCardViewModel> _featuredGames = new();
        private readonly Queue<string> _recentSurpriseGameIds = new();
        private readonly HashSet<string> _recentSurpriseGameIdSet = new(StringComparer.Ordinal);
        private readonly List<GameCardViewModel> _filteredCatalogCards = new();
        private readonly HashSet<GameCardViewModel> _observedDownloadCards = new();
        private GameCardViewModel? _activeTorrentDownload;
        private const int InitialCatalogCardCount = 36;
        private const int CatalogCardBatchSize = 24;

        // Language-neutral state keys. The visible labels come from resources, so the
        // current UI language never decides which tab/sort is active.
        private const string TrendingTab = "Trending";
        private const string TopDownloadsTab = "TopDownloads";
        private const string AlphabeticalTab = "Alphabetical";
        private const string SortByName = "Name";
        private const string SortBySize = "Size";
        private const string SortByStatus = "Status";
        private int _displayedCatalogCardCount;
        private readonly HashSet<string> _portableVersionLookups = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _portableVersionLookupGate = new(2, 2);
        private readonly DispatcherTimer _featuredTimer = new() { Interval = TimeSpan.FromSeconds(8) };
        private int _featuredGameIndex;

        public SettingsViewModel SettingsVM { get; }
        public string ProfileInitials { get; } = CreateRandomProfileInitials();
        public string TimeGreeting
        {
            get
            {
                var hour = DateTime.Now.Hour;
                var resourceKey = hour switch
                {
                    >= 5 and < 12 => "GreetingMorning",
                    >= 12 and < 18 => "GreetingDay",
                    >= 18 and < 23 => "GreetingEvening",
                    _ => "GreetingNight"
                };
                return LocalizationManager.GetString(resourceKey);
            }
        }

        public MainViewModel(
            IGameCatalog catalog,
            IGameManager gameManager,
            IGameLauncher gameLauncher,
            ISettingsService settingsService,
            IGameSearchAggregator searchAggregator,
            IGameArtworkService artworkService,
            ISteamFeaturedGamesService steamFeaturedGamesService,
            NotificationService notifications)
        {
            _catalog = catalog;
            _gameManager = gameManager;
            _gameLauncher = gameLauncher;
            _settingsService = settingsService;
            _searchAggregator = searchAggregator;
            _artworkService = artworkService;
            _steamFeaturedGamesService = steamFeaturedGamesService;
            _notifications = notifications;

            SettingsVM = new SettingsViewModel(_settingsService, _catalog);
            LocalizationManager.LanguageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SectionTitle));
                OnPropertyChanged(nameof(TimeGreeting));
                foreach (var gameCard in AllGameCards)
                    gameCard.NotifyLocalizationChanged();
            };

            NavigateHomeCommand = new RelayCommand(() => CurrentView = "Home");
            NavigateLibraryCommand = new RelayCommand(() => CurrentView = "Library");
            NavigateDownloadsCommand = new RelayCommand(() => CurrentView = "Downloads");
            NavigateSettingsCommand = new RelayCommand(() => CurrentView = "Settings");
            RefreshCatalogCommand = new AsyncRelayCommand(LoadGamesAsync);
            ImportShortcutCommand = new AsyncRelayCommand(ImportShortcutFromPickerAsync);
            ExecuteOnlineSearchCommand = new AsyncRelayCommand(() => ExecuteOnlineSearchAsync(SearchQuery));
            SetDiscoveryTabCommand = new RelayCommand(parameter => SetDiscoveryTab(parameter?.ToString() ?? TrendingTab));
            LoadMoreCatalogGamesCommand = new AsyncRelayCommand(LoadMoreCatalogGamesAsync, CanLoadMoreCatalogGames);
            SurpriseMeCommand = new RelayCommand(ShowSurpriseGame);
            PreviousFeaturedGameCommand = new RelayCommand(_ => ChangeFeaturedGame(-1, true), _ => _featuredGames.Count > 1);
            NextFeaturedGameCommand = new RelayCommand(_ => ChangeFeaturedGame(1, true), _ => _featuredGames.Count > 1);
            SelectFeaturedGameCommand = new RelayCommand(SelectFeaturedGame, _ => _featuredGames.Count > 1);
            SetDownloadsFilterCommand = new RelayCommand(param => SelectedDownloadsFilter = param?.ToString() ?? "All");
            ClearDownloadsSearchCommand = new RelayCommand(() =>
            {
                DownloadsSearchQuery = string.Empty;
                SelectedDownloadsFilter = "All";
            });
            PauseAllDownloadsCommand = new AsyncRelayCommand(PauseAllDownloadsAsync, () => CanPauseAllDownloads);
            ResumeAllDownloadsCommand = new AsyncRelayCommand(ResumeAllDownloadsAsync, () => CanResumeAllDownloads);
            ClearCompletedDownloadsCommand = new RelayCommand(ClearCompletedDownloads, () => CompletedDownloadsCount > 0);
            AddTorrentFileCommand = new AsyncRelayCommand(AddTorrentFromPickerAsync);
            CycleMaxConcurrentDownloadsCommand = new AsyncRelayCommand(CycleMaxConcurrentDownloadsAsync);
            _featuredTimer.Tick += (_, _) => ChangeFeaturedGame(1, false);

            _gameLauncher.GameExited += OnGameExited;
            _gameManager.InstallationStateChanged += OnInstallationStateChanged;

            _catalogView = new ListCollectionView(AllGameCards);
            _catalogView.Filter = FilterCatalog;
            SetDiscoveryTab(TrendingTab);

            _libraryView = new ListCollectionView(AllGameCards);
            _libraryView.Filter = FilterLibrary;
            _libraryView.IsLiveFiltering = true;
            _libraryView.LiveFilteringProperties.Add(nameof(GameCardViewModel.IsInstalled));
            _libraryView.LiveFilteringProperties.Add(nameof(GameCardViewModel.IsDownloading));

            _downloadsView = new ListCollectionView(AllGameCards);
            _downloadsView.Filter = FilterDownloads;
            _downloadsView.IsLiveFiltering = true;
            _downloadsView.LiveFilteringProperties.Add(nameof(GameCardViewModel.IsDownloadQueueItem));
            _downloadsView.LiveFilteringProperties.Add(nameof(GameCardViewModel.State));
            _downloadsView.LiveFilteringProperties.Add(nameof(GameCardViewModel.IsQueued));
            _downloadsView.LiveFilteringProperties.Add(nameof(GameCardViewModel.IsPaused));
            _downloadsView.LiveFilteringProperties.Add(nameof(GameCardViewModel.IsError));
            _downloadsView.LiveFilteringProperties.Add(nameof(GameCardViewModel.IsCompletedDownload));
            _downloadsView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.DownloadSortPriority), ListSortDirection.Ascending));
            _downloadsView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.QueueOrder), ListSortDirection.Ascending));
            _downloadsView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.Name), ListSortDirection.Ascending));
            _downloadsView.IsLiveSorting = true;
            _downloadsView.LiveSortingProperties.Add(nameof(GameCardViewModel.DownloadSortPriority));
            _downloadsView.LiveSortingProperties.Add(nameof(GameCardViewModel.QueueOrder));
            AllGameCards.CollectionChanged += OnAllGameCardsCollectionChanged;
        }

        public RelayCommand NavigateHomeCommand { get; }
        public RelayCommand NavigateLibraryCommand { get; }
        public RelayCommand NavigateDownloadsCommand { get; }
        public RelayCommand NavigateSettingsCommand { get; }
        public AsyncRelayCommand RefreshCatalogCommand { get; }
        public AsyncRelayCommand ImportShortcutCommand { get; }
        public AsyncRelayCommand ExecuteOnlineSearchCommand { get; }
        public RelayCommand SetDiscoveryTabCommand { get; }
        public AsyncRelayCommand LoadMoreCatalogGamesCommand { get; }
        public RelayCommand SurpriseMeCommand { get; }
        public RelayCommand PreviousFeaturedGameCommand { get; }
        public RelayCommand NextFeaturedGameCommand { get; }
        public RelayCommand SelectFeaturedGameCommand { get; }
        public RelayCommand SetDownloadsFilterCommand { get; }
        public RelayCommand ClearDownloadsSearchCommand { get; }
        public AsyncRelayCommand PauseAllDownloadsCommand { get; }
        public AsyncRelayCommand ResumeAllDownloadsCommand { get; }
        public RelayCommand ClearCompletedDownloadsCommand { get; }
        public AsyncRelayCommand AddTorrentFileCommand { get; }
        public AsyncRelayCommand CycleMaxConcurrentDownloadsCommand { get; }
        public ObservableCollection<FeaturedSlideIndicatorViewModel> FeaturedSlideIndicators { get; } = new();
        public ObservableCollection<GameCardViewModel> SurpriseRollItems { get; } = new();
        public bool HasFeaturedCarousel => _featuredGames.Count > 1;

        private GameCardViewModel? _surpriseWinner;
        public GameCardViewModel? SurpriseWinner
        {
            get => _surpriseWinner;
            private set => SetProperty(ref _surpriseWinner, value);
        }

        private bool _isSurpriseCaseOpen;
        public bool IsSurpriseCaseOpen
        {
            get => _isSurpriseCaseOpen;
            private set => SetProperty(ref _isSurpriseCaseOpen, value);
        }

        public bool IsLoadingMoreGames
        {
            get => _isLoadingMoreGames;
            private set
            {
                if (SetProperty(ref _isLoadingMoreGames, value))
                    LoadMoreCatalogGamesCommand.RaiseCanExecuteChanged();
            }
        }

        public BulkObservableCollection<GameCardViewModel> AllGameCards { get; } = new();
        public BulkObservableCollection<GameCardViewModel> DisplayedCatalogGames { get; } = new();

        private GameCardViewModel? _featuredGame;
        public event Action<GameCardViewModel?, GameCardViewModel?>? FeaturedGameChanging;

        public GameCardViewModel? FeaturedGame
        {
            get => _featuredGame;
            private set
            {
                if (ReferenceEquals(_featuredGame, value))
                    return;

                FeaturedGameChanging?.Invoke(_featuredGame, value);
                SetProperty(ref _featuredGame, value);
            }
        }

        private readonly ListCollectionView _catalogView;
        public ICollectionView CatalogGames => _catalogView;

        private readonly ListCollectionView _libraryView;
        public ICollectionView LibraryGames => _libraryView;

        private readonly ListCollectionView _downloadsView;
        public ICollectionView DownloadGames => _downloadsView;
        public bool HasActiveTorrentDownload => _activeTorrentDownload != null;
        public double TorrentDownloadProgressPercent => _activeTorrentDownload?.ProgressPercent ?? 0;

        /// <summary>Aggregated torrent progress for the native Windows taskbar button.</summary>
        public (bool HasProgress, bool IsPaused, bool IsError, ulong Completed, ulong Total) GetTaskbarTorrentProgress()
        {
            var torrentCards = AllGameCards.Where(card => card.IsTorrentDownload && card.IsDownloadQueueItem).ToList();
            var running = torrentCards.Where(card => card.IsDownloading && !card.IsQueued && !card.IsCompletedDownload).ToList();
            var paused = torrentCards.Where(card => card.IsPaused).ToList();
            var errors = torrentCards.Any(card => card.IsError);
            var contributing = running.Concat(paused).Distinct().ToList();

            if (contributing.Count == 0)
                return errors ? (true, false, true, 0UL, 1UL) : (false, false, false, 0UL, 0UL);

            ulong completed = 0;
            ulong total = 0;
            foreach (var card in contributing)
            {
                var taskTotal = Math.Max(0, card.DownloadTotalBytes);
                var taskCompleted = Math.Clamp(card.DownloadedBytes, 0, taskTotal);
                if (taskTotal == 0)
                {
                    // Some torrent sources have no metadata size. Use the real reported
                    // fraction in a common fixed-point unit until a byte total arrives.
                    taskTotal = 1_000_000;
                    taskCompleted = (long)Math.Round(taskTotal * Math.Clamp(card.ProgressPercent / 100.0, 0, 1));
                }

                completed = SaturatingAdd(completed, (ulong)taskCompleted);
                total = SaturatingAdd(total, (ulong)taskTotal);
            }

            return (true, running.Count == 0 && paused.Count > 0, errors, Math.Min(completed, total), total);
        }

        private static ulong SaturatingAdd(ulong left, ulong right)
            => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        /// <summary>
        /// True only while a game is actually being unpacked (repack/installer) or
        /// updated. Closing the launcher then would kill the installer mid-extract and
        /// corrupt the files, so quitting is blocked in that phase. Downloads and
        /// verification are resumable, so they do NOT block closing.
        /// </summary>
        public bool HasActiveInstallation =>
            AllGameCards.Any(card => card.State == InstallState.Installing || card.State == InstallState.Updating);

        private string _currentView = "Home";
        public string CurrentView
        {
            get => _currentView;
            set
            {
                if (SetProperty(ref _currentView, value))
                {
                    OnPropertyChanged(nameof(IsHomeActive));
                    OnPropertyChanged(nameof(IsLibraryActive));
                    OnPropertyChanged(nameof(IsDownloadsActive));
                    OnPropertyChanged(nameof(IsSettingsActive));
                    UpdateFeaturedTimerState();

                    if (value == "Settings")
                    {
                        SettingsVM.RefreshStorageInfo();
                        SettingsVM.RefreshCacheSize();
                    }
                    else if (value == "Downloads")
                    {
                        EnsureDownloadsArtworkLoaded();
                        RefreshActiveTorrentDownload();
                    }
                }
            }
        }

        public bool IsHomeActive => CurrentView == "Home";
        public bool IsLibraryActive => CurrentView == "Library";
        public bool IsDownloadsActive => CurrentView == "Downloads";
        public bool IsSettingsActive => CurrentView == "Settings";

        private CancellationTokenSource? _searchDebounceCts;
        private bool _isSearchingOnline;
        public bool IsSearchingOnline
        {
            get => _isSearchingOnline;
            set => SetProperty(ref _isSearchingOnline, value);
        }

        private string _searchQuery = string.Empty;
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    _onlineSearchResultTitleKeys.Clear();
                    OnPropertyChanged(nameof(SectionTitle));
                    RefreshCatalogView();
                    LoadMoreCatalogGamesCommand.RaiseCanExecuteChanged();
                    if (string.IsNullOrWhiteSpace(value)
                        && _activeBrowseOrder.HasValue
                        && _nextBrowsePage == 0
                        && CanLoadMoreCatalogGames())
                    {
                        _ = LoadMoreCatalogGamesAsync();
                    }
                    TriggerDebouncedOnlineSearch(value);
                }
            }
        }

        private string _librarySearchQuery = string.Empty;
        public string LibrarySearchQuery
        {
            get => _librarySearchQuery;
            set
            {
                if (SetProperty(ref _librarySearchQuery, value))
                {
                    _libraryView.Refresh();
                }
            }
        }


        public string SectionTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(SearchQuery))
                    return LocalizationManager.GetString("SearchResultsFormat", SearchQuery);
                return SelectedDiscoveryTab switch
                {
                    TopDownloadsTab => LocalizationManager.GetString("SectionTopDownloads"),
                    AlphabeticalTab => LocalizationManager.GetString("SectionAlphabetical"),
                    _ => LocalizationManager.GetString("SectionTrending")
                };
            }
        }


        private string _selectedDiscoveryTab = TrendingTab;
        public string SelectedDiscoveryTab
        {
            get => _selectedDiscoveryTab;
            private set
            {
                if (SetProperty(ref _selectedDiscoveryTab, value))
                {
                    OnPropertyChanged(nameof(SectionTitle));
                }
            }
        }

        private void SetDiscoveryTab(string tab)
        {
            SelectedDiscoveryTab = tab;
            _catalogView.SortDescriptions.Clear();

            if (tab == TopDownloadsTab)
            {
                _catalogView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.ActiveDownloadsScore), ListSortDirection.Descending));
                _catalogView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.PopularityScore), ListSortDirection.Descending));
                _catalogView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.ReleaseDateSortKey), ListSortDirection.Descending));
            }
            else if (tab == AlphabeticalTab)
            {
                _catalogView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.Name), ListSortDirection.Ascending));
            }
            else if (tab == TrendingTab)
            {
                _catalogView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.ActiveDownloadsScore), ListSortDirection.Descending));
                _catalogView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.PopularityScore), ListSortDirection.Descending));
                _catalogView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.ReleaseDateSortKey), ListSortDirection.Descending));
            }

            var browseOrder = tab switch
            {
                TopDownloadsTab => GameBrowseOrder.MostDownloaded,
                AlphabeticalTab => GameBrowseOrder.Alphabetical,
                _ => (GameBrowseOrder?)null
            };
            if (_activeBrowseOrder != browseOrder)
            {
                _activeBrowseOrder = browseOrder;
                _nextBrowsePage = 0;
                _hasMoreBrowsePages = browseOrder.HasValue;
                _browseGeneration++;
                LoadMoreCatalogGamesCommand.RaiseCanExecuteChanged();
                if (browseOrder.HasValue)
                    _ = LoadMoreCatalogGamesAsync();
            }

            RefreshCatalogIndexes();
            RefreshCatalogView();
            RefreshFeaturedGames();
        }

        private bool CanLoadMoreCatalogGames()
            => _activeBrowseOrder.HasValue
                && _hasMoreBrowsePages
                && !IsLoadingMoreGames
                && string.IsNullOrWhiteSpace(SearchQuery);

        private async Task LoadMoreCatalogGamesAsync()
        {
            if (!CanLoadMoreCatalogGames() || _activeBrowseOrder is not GameBrowseOrder order)
                return;

            var source = _searchAggregator.Sources
                .OfType<IPagedGameSource>()
                .FirstOrDefault(candidate => string.Equals(candidate.SourceId, "rutor", StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                _hasMoreBrowsePages = false;
                LoadMoreCatalogGamesCommand.RaiseCanExecuteChanged();
                return;
            }

            var generation = _browseGeneration;
            var page = _nextBrowsePage;
            var shouldSkipDuplicatePage = false;
            IsLoadingMoreGames = true;

            try
            {
                var releases = await source.GetGamesPageAsync(page, order);
                if (generation != _browseGeneration)
                    return;

                var addedCount = AddBrowsePage(releases, order);
                _nextBrowsePage = page + 1;
                _hasMoreBrowsePages = releases.Count >= source.PageSize;
                shouldSkipDuplicatePage = addedCount == 0 && _hasMoreBrowsePages;
            }
            catch
            {
                if (generation == _browseGeneration)
                    _hasMoreBrowsePages = false;
            }
            finally
            {
                IsLoadingMoreGames = false;
                LoadMoreCatalogGamesCommand.RaiseCanExecuteChanged();

                if (generation != _browseGeneration && CanLoadMoreCatalogGames())
                    _ = LoadMoreCatalogGamesAsync();
                else if (shouldSkipDuplicatePage && CanLoadMoreCatalogGames())
                    _ = LoadMoreCatalogGamesAsync();
            }
        }

        private int AddBrowsePage(IReadOnlyList<GameReleaseCandidate> releases, GameBrowseOrder order)
        {
            var cardsToAdd = new List<GameCardViewModel>();
            foreach (var release in releases)
            {
                var title = !string.IsNullOrWhiteSpace(release.CleanTitle) ? release.CleanTitle : release.RawTitle;
                var titleKey = GetTitleKey(title);
                if (titleKey.Length == 0)
                    continue;

                var existing = AllGameCards.FirstOrDefault(card =>
                    string.Equals(card.ReleaseCandidate?.SourceId, release.SourceId, StringComparison.OrdinalIgnoreCase)
                    && GetTitleKey(card.Name) == titleKey);
                if (existing != null)
                {
                    var replacesWithPortable = release.VersionType == GameVersionType.Portable
                        && existing.ReleaseCandidate?.VersionType != GameVersionType.Portable;
                    var improvesDownloadRank = order == GameBrowseOrder.MostDownloaded
                        && release.Leechers > existing.ActiveDownloadsScore;
                    if ((!replacesWithPortable && !improvesDownloadRank)
                        || existing.IsInstalled || existing.IsDownloading || existing.IsPaused)
                        continue;

                    AllGameCards.Remove(existing);
                }

                var installation = _gameManager.GetInstallation(release.Id);
                cardsToAdd.Add(new GameCardViewModel(
                    release, installation, _gameManager, _gameLauncher,
                    _settingsService, _searchAggregator, _artworkService, _notifications));
            }

            AllGameCards.AddRange(cardsToAdd);
            RefreshCatalogIndexes();
            RefreshCatalogView(resetDisplayed: false);
            RefreshFeaturedGames();
            SchedulePortableVersionLookups(cardsToAdd.Take(10));
            return cardsToAdd.Count;
        }

        private void RefreshFeaturedGames()
        {
            var currentGameId = FeaturedGame?.Id;
            if (_steamFeaturedGames != null)
            {
                ApplyFeaturedGames(_steamFeaturedGames, currentGameId);
                return;
            }

            var today = DateTime.Today;
            var trendingGames = AllGameCards
                .Where(card => card.ReleaseCandidate != null && GetTitleKey(card.Name).Length > 0)
                .GroupBy(card => GetTitleKey(card.Name), StringComparer.Ordinal)
                .Select(group => group.Aggregate((best, candidate) =>
                    IsPreferredTrendingRelease(candidate, best) ? candidate : best))
                .Where(card => card.ReleaseCandidate is GameReleaseCandidate release
                    && string.Equals(release.SourceId, "rutor", StringComparison.OrdinalIgnoreCase)
                    && release.PublishDate is DateTime published
                    && published.Date >= today.AddDays(-6)
                    && published.Date <= today)
                .OrderByDescending(card => card.ActiveDownloadsScore)
                .ThenByDescending(card => card.PopularityScore)
                .ThenByDescending(card => card.ReleaseDateSortKey)
                .Take(10)
                .ToList();

            if (trendingGames.Count == 0)
            {
                trendingGames = CatalogGames.Cast<GameCardViewModel>().Take(10).ToList();
                if (trendingGames.Count == 0)
                    trendingGames = AllGameCards.Take(10).ToList();
            }

            ApplyFeaturedGames(trendingGames, currentGameId);
        }

        private void ApplyFeaturedGames(IReadOnlyList<GameCardViewModel> games, string? currentGameId)
        {
            _featuredGames.Clear();
            _featuredGames.AddRange(games);
            FeaturedSlideIndicators.Clear();
            for (var index = 0; index < _featuredGames.Count; index++)
                FeaturedSlideIndicators.Add(new FeaturedSlideIndicatorViewModel(index, index == 0));

            _featuredGameIndex = currentGameId == null
                ? 0
                : _featuredGames.FindIndex(card => string.Equals(card.Id, currentGameId, StringComparison.Ordinal));
            if (_featuredGameIndex < 0)
                _featuredGameIndex = 0;

            UpdateFeaturedIndicatorState();
            FeaturedGame = _featuredGames.Count > 0 ? _featuredGames[_featuredGameIndex] : null;
            if (FeaturedGame != null)
                _ = FeaturedGame.EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);
            OnPropertyChanged(nameof(HasFeaturedCarousel));
            PreviousFeaturedGameCommand.RaiseCanExecuteChanged();
            NextFeaturedGameCommand.RaiseCanExecuteChanged();
            SelectFeaturedGameCommand.RaiseCanExecuteChanged();
            UpdateFeaturedTimerState();
        }

        private async Task LoadSteamFeaturedGamesAsync()
        {
            _featuredGamesLoadCts?.Cancel();
            _featuredGamesLoadCts?.Dispose();
            _featuredGamesLoadCts = new CancellationTokenSource();
            var cancellationToken = _featuredGamesLoadCts.Token;

            try
            {
                var progress = new Progress<IReadOnlyList<SteamFeaturedGame>>(matches =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        ApplySteamFeaturedGames(matches);
                });
                var matches = await _steamFeaturedGamesService.GetFeaturedGamesAsync(
                    10, cancellationToken, progress).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    ApplySteamFeaturedGames(matches));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    ApplySteamFeaturedGames(Array.Empty<SteamFeaturedGame>()));
            }
        }

        private void ApplySteamFeaturedGames(IReadOnlyList<SteamFeaturedGame> matches)
        {
            var featuredCards = new List<GameCardViewModel>();
            foreach (var match in matches)
            {
                var candidate = match.Release;
                var titleKey = GetTitleKey(!string.IsNullOrWhiteSpace(candidate.CleanTitle)
                    ? candidate.CleanTitle
                    : candidate.RawTitle);
                var card = AllGameCards.FirstOrDefault(existing =>
                    string.Equals(existing.ReleaseCandidate?.SourceId, "rutor", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(existing.ReleaseCandidate?.Id, candidate.Id, StringComparison.Ordinal)
                        || (titleKey.Length > 0 && GetTitleKey(existing.Name) == titleKey)))
                    ?? _steamFeaturedGames?.FirstOrDefault(existing =>
                        string.Equals(existing.ReleaseCandidate?.Id, candidate.Id, StringComparison.Ordinal));

                if (card == null)
                {
                    card = new GameCardViewModel(candidate, _gameManager.GetInstallation(candidate.Id),
                        _gameManager, _gameLauncher, _settingsService, _searchAggregator, _artworkService, _notifications);
                }

                if (match.Release.VersionType == GameVersionType.Portable
                    && card.ReleaseCandidate?.VersionType != GameVersionType.Portable)
                {
                    card.ApplyPreferredRelease(match.Release);
                }

                card.ApplySteamArtwork(match.SteamGame.CoverUrl, match.SteamGame.BannerUrl);
                if (!featuredCards.Contains(card))
                    featuredCards.Add(card);
            }

            if (featuredCards.Count == 0)
            {
                // Steam featured is unavailable (offline or no matches): keep the
                // current carousel instead of replacing it with the fallback slide.
                if (_steamFeaturedGames is { Count: > 0 })
                    return;

                _steamFeaturedGames = null;
                RefreshFeaturedGames();
                return;
            }

            _steamFeaturedGames = featuredCards;
            ApplyFeaturedGames(_steamFeaturedGames, FeaturedGame?.Id);
        }

        private void SchedulePortableVersionLookups(IEnumerable<GameCardViewModel> cards)
        {
            foreach (var card in cards)
            {
                if (card.ReleaseCandidate is not GameReleaseCandidate release
                    || !string.Equals(release.SourceId, "rutor", StringComparison.OrdinalIgnoreCase)
                    || release.VersionType == GameVersionType.Portable)
                    continue;

                var titleKey = GetTitleKey(card.Name);
                if (titleKey.Length == 0 || !_portableVersionLookups.Add(titleKey))
                    continue;

                _ = ResolvePortableVersionAsync(card);
            }
        }

        private async Task ResolvePortableVersionAsync(GameCardViewModel card)
        {
            await _portableVersionLookupGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var installPath = string.IsNullOrWhiteSpace(_settingsService.Settings.DefaultInstallPath)
                    ? PathHelper.DefaultLibraryPath
                    : _settingsService.Settings.DefaultInstallPath;
                var result = await _searchAggregator.FindAndSelectBestVersionAsync(card.Name, installPath)
                    .ConfigureAwait(false);
                var preferredRelease = result.SelectedCandidate;
                if (preferredRelease?.VersionType != GameVersionType.Portable
                    || !string.Equals(preferredRelease.SourceId, "rutor", StringComparison.OrdinalIgnoreCase))
                    return;

                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (!AllGameCards.Contains(card) || card.IsInstalled || card.IsDownloading || card.IsPaused
                        || card.ReleaseCandidate?.VersionType == GameVersionType.Portable)
                        return;

                    card.ApplyPreferredRelease(preferredRelease);
                    RefreshCatalogIndexes();
                    RefreshCatalogView(resetDisplayed: false);
                });
            }
            catch (Exception ex)
            {
                AppLog.Debug("MainViewModel.PortableLookup", $"Could not resolve a portable version: {ex.Message}");
            }
            finally
            {
                _portableVersionLookupGate.Release();
            }
        }

        private static bool IsPreferredTrendingRelease(GameCardViewModel candidate, GameCardViewModel current)
        {
            var candidateRelease = candidate.ReleaseCandidate!;
            var currentRelease = current.ReleaseCandidate!;
            var candidatePortable = candidateRelease.VersionType == GameVersionType.Portable;
            var currentPortable = currentRelease.VersionType == GameVersionType.Portable;
            if (candidatePortable != currentPortable)
                return candidatePortable;
            if (candidateRelease.SourcePriority != currentRelease.SourcePriority)
                return candidateRelease.SourcePriority < currentRelease.SourcePriority;
            if (candidateRelease.Seeders != currentRelease.Seeders)
                return candidateRelease.Seeders > currentRelease.Seeders;

            return candidateRelease.PublishDate.GetValueOrDefault(DateTime.MinValue)
                > currentRelease.PublishDate.GetValueOrDefault(DateTime.MinValue);
        }

        private void ChangeFeaturedGame(int offset, bool resetAutoTimer)
        {
            if (_featuredGames.Count < 2)
                return;

            _featuredGameIndex = (_featuredGameIndex + offset + _featuredGames.Count) % _featuredGames.Count;
            UpdateFeaturedIndicatorState();
            FeaturedGame = _featuredGames[_featuredGameIndex];
            if (FeaturedGame != null)
                _ = FeaturedGame.EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);

            if (resetAutoTimer)
                RestartFeaturedTimer();
        }

        private void SelectFeaturedGame(object? parameter)
        {
            if (parameter is not int index || index < 0 || index >= _featuredGames.Count
                || index == _featuredGameIndex)
                return;

            _featuredGameIndex = index;
            UpdateFeaturedIndicatorState();
            FeaturedGame = _featuredGames[_featuredGameIndex];
            if (FeaturedGame != null)
                _ = FeaturedGame.EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);
            RestartFeaturedTimer();
        }

        private void UpdateFeaturedIndicatorState()
        {
            foreach (var indicator in FeaturedSlideIndicators)
                indicator.IsActive = indicator.Index == _featuredGameIndex;
        }

        private void RestartFeaturedTimer()
        {
            _featuredTimer.Stop();
            UpdateFeaturedTimerState();
        }

        private void UpdateFeaturedTimerState()
        {
            if (CurrentView == "Home" && _featuredGames.Count > 1)
                _featuredTimer.Start();
            else
                _featuredTimer.Stop();
        }

        private void ShowSurpriseGame()
        {
            var games = CatalogGames.Cast<GameCardViewModel>().ToList();
            if (games.Count == 0)
                games = AllGameCards.ToList();
            if (games.Count == 0)
                return;

            var uninstalledGames = games
                .Where(game => !game.IsInstalled && !game.IsDownloading)
                .ToList();
            if (uninstalledGames.Count > 0)
                games = uninstalledGames;

            var freshGames = games
                .Where(game => !_recentSurpriseGameIdSet.Contains(game.Id))
                .ToList();
            if (freshGames.Count > 0)
            {
                games = freshGames;
            }
            else
            {
                _recentSurpriseGameIds.Clear();
                _recentSurpriseGameIdSet.Clear();
            }

            var winner = games[Random.Shared.Next(games.Count)];
            _recentSurpriseGameIds.Enqueue(winner.Id);
            _recentSurpriseGameIdSet.Add(winner.Id);
            while (_recentSurpriseGameIds.Count > 5)
                _recentSurpriseGameIdSet.Remove(_recentSurpriseGameIds.Dequeue());

            SurpriseRollItems.Clear();
            var decoyGames = games.Where(game => !ReferenceEquals(game, winner)).ToList();
            if (decoyGames.Count == 0)
                decoyGames = games;
            for (var index = 0; index < 25; index++)
                SurpriseRollItems.Add(decoyGames[Random.Shared.Next(decoyGames.Count)]);
            SurpriseRollItems.Add(winner);
            for (var index = 0; index < 8; index++)
                SurpriseRollItems.Add(decoyGames[Random.Shared.Next(decoyGames.Count)]);
            SurpriseWinner = winner;
            IsSurpriseCaseOpen = true;
        }

        public void FinishSurpriseCase()
        {
            var winner = SurpriseWinner;
            IsSurpriseCaseOpen = false;
            SurpriseRollItems.Clear();
            if (winner == null)
                return;

            _featuredGameIndex = _featuredGames.FindIndex(game => ReferenceEquals(game, winner));
            UpdateFeaturedIndicatorState();
            FeaturedGame = winner;
            _ = winner.EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);
            RestartFeaturedTimer();
        }

        public void CancelSurpriseCase()
        {
            IsSurpriseCaseOpen = false;
            SurpriseWinner = null;
            SurpriseRollItems.Clear();
        }

        private static string CreateRandomProfileInitials()
            => new(new[]
            {
                (char)Random.Shared.Next('A', 'Z' + 1),
                (char)Random.Shared.Next('A', 'Z' + 1)
            });

        private string _librarySortBy = SortByName;
        public string LibrarySortBy
        {
            get => _librarySortBy;
            set
            {
                if (SetProperty(ref _librarySortBy, value))
                {
                    ApplyLibrarySorting();
                }
            }
        }

        private Task ImportShortcutFromPickerAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Windows shortcuts (*.lnk)|*.lnk",
                CheckFileExists = true,
                Multiselect = false,
                Title = LocalizationManager.GetString("LibraryAddShortcut")
            };

            return dialog.ShowDialog() == true
                ? ImportShortcutAsync(dialog.FileName)
                : Task.CompletedTask;
        }

        public async Task ImportShortcutAsync(string shortcutPath)
        {
            try
            {
                var shortcut = WindowsShortcutReader.Read(shortcutPath);
                var identity = Path.GetFullPath(shortcut.TargetPath).ToUpperInvariant()
                    + "\n" + shortcut.Arguments;
                var id = "shortcut_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16]
                    .ToLowerInvariant();
                var displayName = Path.GetFileNameWithoutExtension(shortcutPath);

                if (AllGameCards.Any(card => string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    System.Windows.MessageBox.Show(LocalizationManager.GetString("LibraryShortcutAlreadyAdded"),
                        LocalizationManager.GetString("LibraryAddShortcut"),
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }

                var installation = new GameInstallation
                {
                    GameId = id,
                    DisplayName = displayName,
                    InstallPath = shortcut.WorkingDirectory,
                    InstalledVersion = "External shortcut",
                    State = InstallState.Installed,
                    InstalledAt = DateTime.UtcNow,
                    IsExternalShortcut = true,
                    ExternalExecutablePath = shortcut.TargetPath,
                    ExternalLaunchArguments = shortcut.Arguments,
                    ExternalWorkingDirectory = shortcut.WorkingDirectory
                };
                var definition = new GameDefinition
                {
                    Id = id,
                    Name = displayName,
                    Description = shortcut.TargetPath,
                    Executable = shortcut.TargetPath,
                    Version = "External shortcut"
                };

                _gameManager.TrackInstallation(installation);
                await _gameManager.SaveLibraryAsync();
                AllGameCards.Add(new GameCardViewModel(
                    definition, installation, _gameManager, _gameLauncher,
                    _settingsService, _searchAggregator, _artworkService, _notifications));
                RefreshViews();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    LocalizationManager.GetString("LibraryShortcutImportFailed") + Environment.NewLine + ex.Message,
                    LocalizationManager.GetString("LibraryAddShortcut"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public async Task InitializeAsync()
        {
            await _settingsService.LoadAsync();
            SettingsVM.LoadSettings();
            await _gameManager.LoadLibraryAsync();
            await LoadGamesAsync();

            if (_settingsService.Settings.AutoCheckUpdates)
            {
                _ = Task.Run(async () =>
                {
                    await _gameManager.CheckUpdatesAsync();
                });
            }

            // Downloads resume only when the user explicitly clicks "Continue".
        }

        public async Task ShutdownAsync()
        {
            await _gameManager.ShutdownAndSaveAllDownloadsAsync();
        }

        public async Task LoadGamesAsync()
        {
            _portableVersionLookups.Clear();
            IsLoading = true;
            try
            {
                await _catalog.RefreshAsync();
                var games = await _catalog.GetAllGamesAsync();

                AllGameCards.Clear();
                RefreshCatalogIndexes();

                AllGameCards.AddRange(games.Select(g => new GameCardViewModel(
                    g, _gameManager.GetInstallation(g.Id), _gameManager, _gameLauncher,
                    _settingsService, _searchAggregator, _artworkService, _notifications)));

                var knownGameIds = new HashSet<string>(AllGameCards.Select(card => card.Id), StringComparer.OrdinalIgnoreCase);
                var savedInstallations = _gameManager.Installations.Values
                    .Where(installation => (installation.State is InstallState.Installed or InstallState.UpdateAvailable)
                        && !knownGameIds.Contains(installation.GameId)
                        && Directory.Exists(installation.InstallPath))
                    .GroupBy(installation => Path.GetFullPath(installation.InstallPath), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(installation => installation.InstalledAt)
                        .ThenByDescending(installation => installation.InstalledSizeBytes)
                        .First())
                    .ToList();
                AllGameCards.AddRange(savedInstallations.Select(installation =>
                {
                    var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(installation.InstallPath));
                    var displayName = !string.IsNullOrWhiteSpace(installation.DisplayName)
                        ? installation.DisplayName
                        : !string.IsNullOrWhiteSpace(folderName) ? folderName : installation.GameId;
                    var definition = new GameDefinition
                    {
                        Id = installation.GameId,
                        Name = displayName,
                        InstallSubfolder = folderName,
                        Version = installation.InstalledVersion,
                        SizeBytes = installation.InstalledSizeBytes
                    };
                    return new GameCardViewModel(
                        definition, installation, _gameManager, _gameLauncher,
                        _settingsService, _searchAggregator, _artworkService, _notifications, isLibraryRecoveryEntry: true);
                }));

                var pendingDownloadInstallations = _gameManager.Installations.Values
                    .Where(installation => (installation.State is InstallState.Paused or InstallState.Downloading
                            or InstallState.Verifying or InstallState.Installing
                            || (installation.State == InstallState.Error && (installation.PendingRelease != null || installation.DownloadedBytes > 0)))
                        && !knownGameIds.Contains(installation.GameId))
                    .ToList();

                foreach (var installation in pendingDownloadInstallations)
                {
                    var displayName = !string.IsNullOrWhiteSpace(installation.DisplayName)
                        ? installation.DisplayName
                        : installation.PendingRelease?.CleanTitle ?? installation.GameId;
                    var definition = new GameDefinition
                    {
                        Id = installation.GameId,
                        Name = displayName,
                        InstallSubfolder = Path.GetFileName(Path.TrimEndingDirectorySeparator(installation.InstallPath ?? string.Empty)),
                        Version = installation.PendingRelease?.Version ?? installation.InstalledVersion,
                        DownloadSizeBytes = installation.PendingRelease?.DownloadSizeBytes ?? 0,
                        SizeBytes = installation.PendingRelease?.EstimatedInstalledSizeBytes ?? 0
                    };
                    if (installation.PendingRelease != null)
                    {
                        definition.DownloadSources.Add(new DownloadSource
                        {
                            Type = "torrent",
                            Priority = 1,
                            TorrentFileUrl = installation.PendingRelease.TorrentFileUrl,
                            MagnetLink = installation.PendingRelease.MagnetLink,
                            InstallerMode = installation.PendingRelease.VersionType == GameVersionType.Repack ? "repack" : null
                        });
                    }

                    var card = new GameCardViewModel(
                        definition, installation, _gameManager, _gameLauncher,
                        _settingsService, _searchAggregator, _artworkService, _notifications, isLibraryRecoveryEntry: false);

                    if (installation.PendingRelease != null)
                    {
                        card.ReleaseCandidate = installation.PendingRelease;
                    }

                    AllGameCards.Add(card);
                    knownGameIds.Add(card.Id);
                }

                RefreshCatalogIndexes();

                RefreshFeaturedGames();
                RefreshViews();
                _ = LoadSteamFeaturedGamesAsync();

                if (_activeBrowseOrder.HasValue)
                {
                    _browseGeneration++;
                    _nextBrowsePage = 0;
                    _hasMoreBrowsePages = true;
                    LoadMoreCatalogGamesCommand.RaiseCanExecuteChanged();
                    _ = LoadMoreCatalogGamesAsync();
                }

                // Show the last successful snapshot immediately so the catalogue is not
                // almost empty while the trackers are being queried, then refresh it in
                // the background and append/replace the new releases.
                AddLatestReleaseCards(_searchAggregator.GetCachedLatestGames());

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var latestReleases = await _searchAggregator.GetLatestGamesAsync();
                        if (latestReleases.Count > 0)
                        {
                            System.Windows.Application.Current?.Dispatcher?.Invoke(
                                () => AddLatestReleaseCards(latestReleases));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("MainViewModel.LatestReleases", "Background refresh of the latest releases failed.", ex);
                    }
                });
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>Adds release candidates (cached or freshly fetched) as catalogue cards.</summary>
        private void AddLatestReleaseCards(IReadOnlyList<GameReleaseCandidate> latestReleases)
        {
            if (latestReleases.Count == 0)
                return;

            var builtInTitleKeys = new HashSet<string>(AllGameCards
                .Where(card => card.ReleaseCandidate == null)
                .Select(card => GetTitleKey(card.Name)));
            var existingSourceTitleKeys = new HashSet<string>(AllGameCards
                .Where(card => card.ReleaseCandidate != null)
                .Select(card => $"{card.ReleaseCandidate!.SourceId}|{GetTitleKey(card.Name)}"));
            var installedTitleKeys = new HashSet<string>(AllGameCards
                .Where(card => card.IsInstalled)
                .Select(card => GetTitleKey(card.Name)));
            var existingIds = new HashSet<string>(AllGameCards.Select(card => card.Id));
            var cardsToAdd = new List<GameCardViewModel>();

            foreach (var rel in latestReleases)
            {
                var title = !string.IsNullOrWhiteSpace(rel.CleanTitle) ? rel.CleanTitle : rel.RawTitle;
                var titleKey = GetTitleKey(title);
                if (titleKey.Length == 0)
                    continue;

                var sourceTitleKey = $"{rel.SourceId}|{titleKey}";
                var isRecentRutorRelease = string.Equals(
                        rel.SourceId, "rutor", StringComparison.OrdinalIgnoreCase)
                    && rel.PublishDate is DateTime publishDate
                    && publishDate.Date >= DateTime.Today.AddDays(-6)
                    && publishDate.Date <= DateTime.Today;

                if (existingIds.Contains(rel.Id)
                    || installedTitleKeys.Contains(titleKey)
                    || (builtInTitleKeys.Contains(titleKey) && !isRecentRutorRelease)
                    || existingSourceTitleKeys.Contains(sourceTitleKey))
                    continue;

                var install = _gameManager.GetInstallation(rel.Id);
                var card = new GameCardViewModel(rel, install, _gameManager, _gameLauncher,
                    _settingsService, _searchAggregator, _artworkService, _notifications);
                cardsToAdd.Add(card);
                existingSourceTitleKeys.Add(sourceTitleKey);
                existingIds.Add(rel.Id);
            }

            if (cardsToAdd.Count == 0)
                return;

            AllGameCards.AddRange(cardsToAdd);
            RefreshCatalogIndexes();
            RefreshCatalogView(resetDisplayed: false);
            RefreshFeaturedGames();
            SchedulePortableVersionLookups(CatalogGames.Cast<GameCardViewModel>().Take(10));
        }

        private void TriggerDebouncedOnlineSearch(string query)
        {
            _searchDebounceCts?.Cancel();
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            {
                IsSearchingOnline = false;
                return;
            }

            IsSearchingOnline = false;
            _searchDebounceCts = new CancellationTokenSource();
            var token = _searchDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token);
                    if (!token.IsCancellationRequested)
                    {
                        await ExecuteOnlineSearchAsync(query, token);
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        public async Task ExecuteOnlineSearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return;

            IsSearchingOnline = true;
            try
            {
                var candidates = await _searchAggregator.SearchAllSourcesAsync(query.Trim(), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (cancellationToken.IsCancellationRequested ||
                        !string.Equals(SearchQuery.Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase))
                        return;

                    var existingIds = new HashSet<string>(AllGameCards.Select(c => c.Id));
                    var cardsToAdd = new List<GameCardViewModel>();
                    var added = false;
                    var preferredCandidates = candidates
                        .GroupBy(c => GetTitleKey(!string.IsNullOrWhiteSpace(c.CleanTitle) ? c.CleanTitle : c.RawTitle))
                        .Select(group => group
                            .OrderBy(c => c.Seeders > 0 ? 0 : 1)
                            .ThenBy(c => (c.VersionType == GameVersionType.Portable && c.Seeders >= 5) ? 0 : 1)
                            .ThenBy(c => c.SourcePriority)
                            .ThenByDescending(c => c.Seeders)
                            .ThenByDescending(c => c.PublishDate ?? DateTime.MinValue)
                            .First())
                        .ToList();

                    foreach (var cand in preferredCandidates)
                    {
                        var key = GetTitleKey(!string.IsNullOrWhiteSpace(cand.CleanTitle) ? cand.CleanTitle : cand.RawTitle);
                        if (key.Length > 0)
                            _onlineSearchResultTitleKeys.Add(key);
                        if (existingIds.Contains(cand.Id))
                            continue;

                        var existingCard = AllGameCards.FirstOrDefault(card => GetTitleKey(card.Name) == key);
                        if (existingCard != null)
                        {
                            var currentRelease = existingCard.ReleaseCandidate;
                            var canReplaceRepack = cand.VersionType == GameVersionType.Portable
                                && cand.Seeders >= 5
                                && currentRelease != null
                                && currentRelease.VersionType != GameVersionType.Portable
                                && !existingCard.IsInstalled
                                && !existingCard.IsDownloading
                                && !existingCard.IsPaused;
                            if (canReplaceRepack)
                            {
                                var index = AllGameCards.IndexOf(existingCard);
                                AllGameCards[index] = new GameCardViewModel(cand, _gameManager.GetInstallation(cand.Id),
                                    _gameManager, _gameLauncher, _settingsService, _searchAggregator, _artworkService, _notifications);
                                existingIds.Remove(existingCard.Id);
                                existingIds.Add(cand.Id);
                                added = true;
                            }
                            continue;
                        }

                        var install = _gameManager.GetInstallation(cand.Id);
                        var card = new GameCardViewModel(cand, install, _gameManager, _gameLauncher, _settingsService, _searchAggregator, _artworkService, _notifications);
                        cardsToAdd.Add(card);
                        existingIds.Add(cand.Id);
                        added = true;
                    }

                    if (cardsToAdd.Count > 0)
                        AllGameCards.AddRange(cardsToAdd);
                    if (added)
                        RefreshCatalogIndexes();
                    if (preferredCandidates.Count > 0)
                        RefreshCatalogView();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLog.Warn("MainViewModel.OnlineSearch", "Online search failed.", ex);
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                    IsSearchingOnline = false;
            }
        }

        private void RefreshCatalogIndexes()
        {
            _builtInTitleKeys.Clear();
            _rutorTitleKeys.Clear();
            _preferredReleaseCards.Clear();

            foreach (var card in AllGameCards)
            {
                var titleKey = GetTitleKey(card.Name);
                if (titleKey.Length == 0)
                    continue;

                if (card.ReleaseCandidate is not GameReleaseCandidate release)
                {
                    _builtInTitleKeys.Add(titleKey);
                    continue;
                }

                if (string.Equals(release.SourceId, "rutor", StringComparison.OrdinalIgnoreCase))
                    _rutorTitleKeys.Add(titleKey);

                if (!_preferredReleaseCards.TryGetValue(titleKey, out var current)
                    || IsPreferredReleaseCard(card, current))
                {
                    _preferredReleaseCards[titleKey] = card;
                }
            }
        }

        private bool IsPreferredReleaseCard(GameCardViewModel candidate, GameCardViewModel current)
        {
            var candidateRelease = candidate.ReleaseCandidate!;
            var currentRelease = current.ReleaseCandidate!;
            if (SelectedDiscoveryTab == TopDownloadsTab)
            {
                if (candidateRelease.Leechers != currentRelease.Leechers)
                    return candidateRelease.Leechers > currentRelease.Leechers;
                if (candidateRelease.Seeders != currentRelease.Seeders)
                    return candidateRelease.Seeders > currentRelease.Seeders;
            }
            else
            {
                var candidatePortable = candidateRelease.VersionType == GameVersionType.Portable;
                var currentPortable = currentRelease.VersionType == GameVersionType.Portable;
                if (candidatePortable != currentPortable)
                    return candidatePortable;
                if (candidateRelease.SourcePriority != currentRelease.SourcePriority)
                    return candidateRelease.SourcePriority < currentRelease.SourcePriority;
                if (candidateRelease.Seeders != currentRelease.Seeders)
                    return candidateRelease.Seeders > currentRelease.Seeders;
            }

            return candidateRelease.PublishDate.GetValueOrDefault(DateTime.MinValue)
                > currentRelease.PublishDate.GetValueOrDefault(DateTime.MinValue);
        }

        // Suffixes that are stripped before deduplication so that e.g.
        // "Call of Duty: Modern Warfare 4" and "Call of Duty: Modern Warfare 4 Campaign"
        // are treated as the same game in the trending section.
        private static readonly System.Text.RegularExpressions.Regex _titleSuffixRx =
            new(@"\b(campaign|prologue|epilogue|chapter\s*\d*|part\s*\d*|episode\s*\d*|remastered|remake|definitive\s*edition|complete\s*edition|gold\s*edition|goty|game\s*of\s*the\s*year)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string GetTitleKey(string title)
        {
            // Strip common game-mode/edition suffixes so near-duplicate titles group together.
            var stripped = _titleSuffixRx.Replace(title.Trim(), string.Empty).Trim();
            return new string(stripped.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        public void LoadMoreDisplayedCatalogGames()
        {
            if (_displayedCatalogCardCount >= _filteredCatalogCards.Count)
                return;

            var nextCount = Math.Min(_displayedCatalogCardCount + CatalogCardBatchSize, _filteredCatalogCards.Count);
            var nextCards = _filteredCatalogCards
                .Skip(_displayedCatalogCardCount)
                .Take(nextCount - _displayedCatalogCardCount)
                .ToList();
            DisplayedCatalogGames.AppendRange(nextCards);
            _displayedCatalogCardCount = nextCount;
        }

        private void RefreshCatalogView(bool resetDisplayed = true)
        {
            var displayedCount = resetDisplayed
                ? InitialCatalogCardCount
                : Math.Max(_displayedCatalogCardCount, InitialCatalogCardCount);

            _catalogView.Refresh();
            _filteredCatalogCards.Clear();
            _filteredCatalogCards.AddRange(_catalogView.Cast<GameCardViewModel>());
            _displayedCatalogCardCount = Math.Min(displayedCount, _filteredCatalogCards.Count);
            var displayedCards = _filteredCatalogCards.Take(_displayedCatalogCardCount).ToList();
            bool hasChanged = DisplayedCatalogGames.Count != displayedCards.Count;
            if (!hasChanged)
            {
                for (int i = 0; i < displayedCards.Count; i++)
                {
                    if (!ReferenceEquals(DisplayedCatalogGames[i], displayedCards[i]))
                    {
                        hasChanged = true;
                        break;
                    }
                }
            }

            if (hasChanged)
            {
                DisplayedCatalogGames.SyncTo(displayedCards);
            }
        }

        public void SetVisibleArtworkCards(IReadOnlyList<GameCardViewModel> cards)
        {
            // Only cards that are actually inside the viewport are allowed to start a
            // network artwork lookup. Off-screen cards load when they scroll into view.
            foreach (var card in cards)
            {
                if (card == null || card.HasCoverArtwork)
                    continue;

                _ = card.EnsureArtworkLoadedAsync(priority: ArtworkPriority.Visible);
            }
        }

        private bool FilterCatalog(object obj)
        {
            if (obj is not GameCardViewModel card || card.IsLibraryRecoveryEntry) return false;

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                var q = SearchQuery.Trim();
                var nameMatch = GameSearchQueryMatcher.MatchesTitleCharacters(q, card.Name)
                    || (card.ReleaseCandidate != null && (
                        (!string.IsNullOrEmpty(card.ReleaseCandidate.AltTitle) && GameSearchQueryMatcher.MatchesTitleCharacters(q, card.ReleaseCandidate.AltTitle))
                        || (!string.IsNullOrEmpty(card.ReleaseCandidate.RawTitle) && GameSearchQueryMatcher.MatchesTitleCharacters(q, card.ReleaseCandidate.RawTitle))
                    ));
                var onlineSearchMatch = _onlineSearchResultTitleKeys.Contains(GetTitleKey(card.Name))
                    || (card.ReleaseCandidate != null && !string.IsNullOrEmpty(card.ReleaseCandidate.AltTitle) && _onlineSearchResultTitleKeys.Contains(GetTitleKey(card.ReleaseCandidate.AltTitle)));
                if (!nameMatch && !onlineSearchMatch)
                {
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(SearchQuery)
                && SelectedDiscoveryTab == TopDownloadsTab
                && card.ReleaseCandidate == null
                && _rutorTitleKeys.Contains(GetTitleKey(card.Name)))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(SearchQuery)
                && SelectedDiscoveryTab == TrendingTab
                && (card.ReleaseCandidate is not GameReleaseCandidate trendRelease
                    || !string.Equals(trendRelease.SourceId, "rutor", StringComparison.OrdinalIgnoreCase)
                    || trendRelease.PublishDate is not DateTime trendDate
                    || trendDate.Date < DateTime.Today.AddDays(-6)
                    || trendDate.Date > DateTime.Today))
            {
                return false;
            }

            if (card.ReleaseCandidate != null)
            {
                var titleKey = GetTitleKey(card.Name);
                if (string.IsNullOrWhiteSpace(SearchQuery)
                    && SelectedDiscoveryTab != TrendingTab
                    && SelectedDiscoveryTab != TopDownloadsTab
                    && _builtInTitleKeys.Contains(titleKey))
                {
                    return false;
                }
                if (!_preferredReleaseCards.TryGetValue(titleKey, out var preferredSourceCard)
                    || !ReferenceEquals(card, preferredSourceCard))
                    return false;
            }


            return true;
        }

        private bool FilterLibrary(object obj)
        {
            if (obj is not GameCardViewModel card) return false;
            if (!card.IsInstalled && !card.IsDownloading) return false;

            if (!string.IsNullOrWhiteSpace(LibrarySearchQuery))
            {
                var q = LibrarySearchQuery.Trim();
                var match = GameSearchQueryMatcher.MatchesTitleCharacters(q, card.Name)
                    || (card.ReleaseCandidate != null && (
                        (!string.IsNullOrEmpty(card.ReleaseCandidate.AltTitle) && GameSearchQueryMatcher.MatchesTitleCharacters(q, card.ReleaseCandidate.AltTitle))
                        || (!string.IsNullOrEmpty(card.ReleaseCandidate.RawTitle) && GameSearchQueryMatcher.MatchesTitleCharacters(q, card.ReleaseCandidate.RawTitle))
                    ));
                if (!match)
                    return false;
            }

            return true;
        }

        private string _downloadsSearchQuery = string.Empty;
        public string DownloadsSearchQuery
        {
            get => _downloadsSearchQuery;
            set
            {
                if (SetProperty(ref _downloadsSearchQuery, value))
                {
                    _downloadsView.Refresh();
                    OnPropertyChanged(nameof(HasFilteredDownloadTasks));
                    OnPropertyChanged(nameof(IsDownloadsFilterEmpty));
                }
            }
        }

        private string _selectedDownloadsFilter = "All";
        public string SelectedDownloadsFilter
        {
            get => _selectedDownloadsFilter;
            set
            {
                if (SetProperty(ref _selectedDownloadsFilter, value))
                {
                    _downloadsView.Refresh();
                    OnPropertyChanged(nameof(IsFilterAllSelected));
                    OnPropertyChanged(nameof(IsFilterActiveSelected));
                    OnPropertyChanged(nameof(IsFilterQueuedSelected));
                    OnPropertyChanged(nameof(IsFilterPausedSelected));
                    OnPropertyChanged(nameof(IsFilterCompletedSelected));
                    OnPropertyChanged(nameof(IsFilterErrorSelected));
                    OnPropertyChanged(nameof(HasFilteredDownloadTasks));
                    OnPropertyChanged(nameof(IsDownloadsFilterEmpty));
                }
            }
        }

        public bool IsFilterAllSelected => SelectedDownloadsFilter == "All";
        public bool IsFilterActiveSelected => SelectedDownloadsFilter == "Active";
        public bool IsFilterQueuedSelected => SelectedDownloadsFilter == "Queued";
        public bool IsFilterPausedSelected => SelectedDownloadsFilter == "Paused";
        public bool IsFilterCompletedSelected => SelectedDownloadsFilter == "Completed";
        public bool IsFilterErrorSelected => SelectedDownloadsFilter == "Error";

        public int TotalDownloadTasksCount => AllGameCards.Count(c => c.IsDownloadQueueItem);
        public int ActiveDownloadsCount => AllGameCards.Count(c => c.IsDownloadQueueItem && c.IsDownloading && !c.IsQueued && !c.IsCompletedDownload);
        public int QueuedDownloadsCount => AllGameCards.Count(c => c.IsDownloadQueueItem && c.IsQueued);
        public int PausedDownloadsCount => AllGameCards.Count(c => c.IsDownloadQueueItem && c.IsPaused);
        public int CompletedDownloadsCount => AllGameCards.Count(c => c.IsDownloadQueueItem && c.IsCompletedDownload);
        public int ErrorDownloadsCount => AllGameCards.Count(c => c.IsDownloadQueueItem && (c.IsError || c.HasDownloadError));
        public bool HasDownloadTasks => TotalDownloadTasksCount > 0;
        public bool HasFilteredDownloadTasks => !_downloadsView.IsEmpty;
        public bool IsDownloadsFilterEmpty => HasDownloadTasks && _downloadsView.IsEmpty;
        public bool CanPauseAllDownloads => ActiveDownloadsCount > 0 || QueuedDownloadsCount > 0;
        public bool CanResumeAllDownloads => PausedDownloadsCount > 0 || ErrorDownloadsCount > 0;
        public bool HasCompletedDownloads => CompletedDownloadsCount > 0;
        public bool HasErrorDownloads => ErrorDownloadsCount > 0;
        public int MaxConcurrentDownloads => Math.Clamp(_settingsService.Settings.MaxConcurrentDownloads, 1, 5);

        public long TotalDownloadSpeedBps => AllGameCards
            .Where(c => c.IsDownloadQueueItem && c.IsDownloading && !c.IsQueued)
            .Sum(c => c.CurrentDownloadSpeedBps);

        public long TotalUploadSpeedBps => AllGameCards
            .Where(c => c.IsDownloadQueueItem && c.IsDownloading && !c.IsQueued)
            .Sum(c => c.CurrentUploadSpeedBps);

        public bool HasActiveDownloadSpeed => TotalDownloadSpeedBps > 0;

        public string TotalDownloadSpeedText => TotalDownloadSpeedBps > 0
            ? LocalizationManager.GetString("SpeedUnitFormat", PathHelper.FormatBytes(TotalDownloadSpeedBps))
            : "—";

        public string TotalUploadSpeedText => TotalUploadSpeedBps > 0
            ? LocalizationManager.GetString("SpeedUnitFormat", PathHelper.FormatBytes(TotalUploadSpeedBps))
            : "—";

        private void EnsureDownloadsArtworkLoaded()
        {
            foreach (var card in AllGameCards.Where(c => c.IsDownloadQueueItem))
            {
                if (!card.HasCoverArtwork || !card.HasBannerArtwork)
                    _ = card.EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);
            }
        }

        private async Task PauseAllDownloadsAsync()
        {
            var targets = AllGameCards
                .Where(c => c.IsDownloadQueueItem && (c.IsDownloading || c.IsQueued))
                .ToList();
            foreach (var card in targets)
            {
                if (card.PauseCommand.CanExecute(null))
                    await card.PauseCommand.ExecuteAsync();
            }
            RefreshActiveTorrentDownload();
        }

        private async Task ResumeAllDownloadsAsync()
        {
            var targets = AllGameCards
                .Where(c => c.IsDownloadQueueItem && (c.IsPaused || c.IsError))
                .OrderBy(c => c.QueueOrder > 0 ? c.QueueOrder : int.MaxValue)
                .ToList();
            foreach (var card in targets)
            {
                if (card.ResumeCommand.CanExecute(null))
                    _ = card.ResumeCommand.ExecuteAsync();
                else if (card.RetryDownloadCommand.CanExecute(null))
                    _ = card.RetryDownloadCommand.ExecuteAsync();
                await Task.Delay(50);
            }
            RefreshActiveTorrentDownload();
        }

        private void ClearCompletedDownloads()
        {
            _gameManager.ClearCompletedDownloads();
            if (SelectedDownloadsFilter == "Completed")
                SelectedDownloadsFilter = "All";
            _downloadsView.Refresh();
            RefreshActiveTorrentDownload();
        }

        private Task AddTorrentFromPickerAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "BitTorrent files (*.torrent)|*.torrent",
                CheckFileExists = true,
                Multiselect = false,
                Title = LocalizationManager.GetString("DownloadAddTorrentButton", "Добавить .torrent")
            };

            return dialog.ShowDialog() == true
                ? InstallTorrentFileAsync(dialog.FileName)
                : Task.CompletedTask;
        }

        private async Task CycleMaxConcurrentDownloadsAsync()
        {
            var current = Math.Clamp(_settingsService.Settings.MaxConcurrentDownloads, 1, 3);
            var next = current >= 3 ? 1 : current + 1;
            _settingsService.Settings.MaxConcurrentDownloads = next;
            OnPropertyChanged(nameof(MaxConcurrentDownloads));
            _gameManager.NotifyMaxConcurrentDownloadsChanged();
            await _settingsService.SaveAsync();
        }

        private bool FilterDownloads(object obj)
        {
            if (obj is not GameCardViewModel card || !card.IsDownloadQueueItem)
                return false;

            if (!string.IsNullOrWhiteSpace(DownloadsSearchQuery))
            {
                var q = DownloadsSearchQuery.Trim();
                var match = GameSearchQueryMatcher.MatchesTitleCharacters(q, card.Name)
                    || (card.ReleaseCandidate != null && (
                        (!string.IsNullOrEmpty(card.ReleaseCandidate.AltTitle) && GameSearchQueryMatcher.MatchesTitleCharacters(q, card.ReleaseCandidate.AltTitle))
                        || (!string.IsNullOrEmpty(card.ReleaseCandidate.RawTitle) && GameSearchQueryMatcher.MatchesTitleCharacters(q, card.ReleaseCandidate.RawTitle))
                    ));
                if (!match)
                    return false;
            }

            return SelectedDownloadsFilter switch
            {
                "Active" => card.IsDownloading && !card.IsQueued && !card.IsCompletedDownload,
                "Queued" => card.IsQueued,
                "Paused" => card.IsPaused,
                "Completed" => card.IsCompletedDownload,
                "Error" => card.IsError || card.HasDownloadError,
                _ => true
            };
        }

        private void OnAllGameCardsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            var currentCards = new HashSet<GameCardViewModel>(AllGameCards);
            foreach (var card in _observedDownloadCards.Except(currentCards).ToArray())
            {
                card.PropertyChanged -= OnDownloadCardPropertyChanged;
                _observedDownloadCards.Remove(card);
            }

            foreach (var card in currentCards.Except(_observedDownloadCards))
            {
                card.PropertyChanged += OnDownloadCardPropertyChanged;
                _observedDownloadCards.Add(card);
            }

            RefreshActiveTorrentDownload();
        }

        private void OnDownloadCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GameCardViewModel.Progress))
            {
                // Throttled telemetry updates: avoid full queue recomputation on every progress tick
                OnPropertyChanged(nameof(TorrentDownloadProgressPercent));
                OnPropertyChanged(nameof(TotalDownloadSpeedBps));
                OnPropertyChanged(nameof(TotalUploadSpeedBps));
                OnPropertyChanged(nameof(HasActiveDownloadSpeed));
                OnPropertyChanged(nameof(TotalDownloadSpeedText));
                OnPropertyChanged(nameof(TotalUploadSpeedText));
            }
            else if (e.PropertyName is nameof(GameCardViewModel.State)
                or nameof(GameCardViewModel.IsQueued)
                or nameof(GameCardViewModel.IsCompletedDownload)
                or nameof(GameCardViewModel.IsDownloadQueueItem)
                or nameof(GameCardViewModel.IsActiveTorrentDownload)
                or nameof(GameCardViewModel.HasDownloadError))
            {
                RefreshActiveTorrentDownload();
            }
        }

        private void RefreshActiveTorrentDownload()
        {
            var selected = AllGameCards.FirstOrDefault(card => card.IsActiveTorrentDownload);
            if (!ReferenceEquals(_activeTorrentDownload, selected))
            {
                _activeTorrentDownload = selected;
                OnPropertyChanged(nameof(HasActiveTorrentDownload));
            }

            OnPropertyChanged(nameof(TorrentDownloadProgressPercent));
            OnPropertyChanged(nameof(TotalDownloadTasksCount));
            OnPropertyChanged(nameof(ActiveDownloadsCount));
            OnPropertyChanged(nameof(QueuedDownloadsCount));
            OnPropertyChanged(nameof(PausedDownloadsCount));
            OnPropertyChanged(nameof(CompletedDownloadsCount));
            OnPropertyChanged(nameof(ErrorDownloadsCount));
            OnPropertyChanged(nameof(HasDownloadTasks));
            OnPropertyChanged(nameof(HasFilteredDownloadTasks));
            OnPropertyChanged(nameof(IsDownloadsFilterEmpty));
            OnPropertyChanged(nameof(CanPauseAllDownloads));
            OnPropertyChanged(nameof(CanResumeAllDownloads));
            OnPropertyChanged(nameof(HasCompletedDownloads));
            OnPropertyChanged(nameof(HasErrorDownloads));
            OnPropertyChanged(nameof(TotalDownloadSpeedBps));
            OnPropertyChanged(nameof(TotalUploadSpeedBps));
            OnPropertyChanged(nameof(HasActiveDownloadSpeed));
            OnPropertyChanged(nameof(TotalDownloadSpeedText));
            OnPropertyChanged(nameof(TotalUploadSpeedText));
            OnPropertyChanged(nameof(MaxConcurrentDownloads));

            PauseAllDownloadsCommand.RaiseCanExecuteChanged();
            ResumeAllDownloadsCommand.RaiseCanExecuteChanged();
            ClearCompletedDownloadsCommand.RaiseCanExecuteChanged();
        }

        private void RefreshViews()
        {
            RefreshCatalogView();
            _libraryView.Refresh();
            ApplyLibrarySorting();
        }

        private void ApplyLibrarySorting()
        {
            _libraryView.SortDescriptions.Clear();
            switch (LibrarySortBy)
            {
                case SortBySize:
                    _libraryView.SortDescriptions.Add(new SortDescription("SizeDisplay", ListSortDirection.Descending));
                    break;
                case SortByStatus:
                    _libraryView.SortDescriptions.Add(new SortDescription("State", ListSortDirection.Ascending));
                    break;
                case SortByName:
                default:
                    _libraryView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                    break;
            }
        }

        private void OnGameExited(object? sender, GameRunStatus status)
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                var card = AllGameCards.FirstOrDefault(c => c.Id == status.GameId);
                card?.NotifyRunningChanged();
            });
        }

        private void OnInstallationStateChanged(object? sender, GameInstallation installation)
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null)
                return;

            void Apply()
            {
                var card = AllGameCards.FirstOrDefault(c => string.Equals(c.Id, installation.GameId, StringComparison.OrdinalIgnoreCase));
                card?.UpdateInstallation(installation);
                _libraryView.Refresh();
                _downloadsView.Refresh();
                RefreshActiveTorrentDownload();
            }

            if (d.CheckAccess())
                Apply();
            else
                d.BeginInvoke(Apply);
        }

        public async Task InstallTorrentFileAsync(string torrentFilePath)
        {
            if (string.IsNullOrWhiteSpace(torrentFilePath) || !File.Exists(torrentFilePath))
                return;

            try
            {
                var torrentBytes = await File.ReadAllBytesAsync(torrentFilePath).ConfigureAwait(false);
                var meta = TorrentMetadataParser.Parse(torrentBytes);

                var title = !string.IsNullOrWhiteSpace(meta.Name)
                    ? meta.Name
                    : Path.GetFileNameWithoutExtension(torrentFilePath);

                var defaultBase = _settingsService.Settings.DefaultInstallPath;
                if (string.IsNullOrWhiteSpace(defaultBase))
                    defaultBase = PathHelper.DefaultLibraryPath;
                PathHelper.EnsureDirectory(defaultBase);

                var identity = $"{title.Trim().ToLowerInvariant()}|{meta.TotalSize}";
                var safeId = "custom-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
                var candidate = new GameReleaseCandidate
                {
                    Id = safeId,
                    CleanTitle = title,
                    RawTitle = title,
                    TorrentFileUrl = torrentFilePath,
                    SourceName = LocalizationManager.GetString("SourceTorrentFile"),
                    SourceId = "local_torrent",
                    DownloadSizeBytes = meta.TotalSize,
                    EstimatedInstalledSizeBytes = meta.TotalSize,
                    VersionType = GameVersionType.Portable
                };

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var card = AllGameCards.FirstOrDefault(c => string.Equals(c.Id, safeId, StringComparison.OrdinalIgnoreCase));
                    if (card == null)
                    {
                        card = new GameCardViewModel(
                            candidate,
                            _gameManager.GetInstallation(safeId),
                            _gameManager,
                            _gameLauncher,
                            _settingsService,
                            _searchAggregator,
                            _artworkService,
                            _notifications);
                        AllGameCards.Insert(0, card);
                    }

                    CurrentView = "Downloads";
                    _ = card.EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);

                    // Trigger install
                    if (card.InstallCommand.CanExecute(null))
                    {
                        await card.InstallCommand.ExecuteAsync();
                    }
                    else if (card.ResumeCommand.CanExecute(null))
                    {
                        await card.ResumeCommand.ExecuteAsync();
                    }
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        LocalizationManager.GetString("InstallSearchErrorFormat", ex.Message),
                        LocalizationManager.GetString("StatusError"),
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                });
            }
        }
    }
}
