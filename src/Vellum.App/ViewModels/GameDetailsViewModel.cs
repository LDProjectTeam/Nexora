using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Vellum.App.Services;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.App.ViewModels
{
    public enum GameDetailsTab
    {
        Overview = 0,
        Specs = 1,
        Storage = 2
    }

    public enum VersionLookupStatusType
    {
        Checking,
        PortableFound,
        RepackOnly,
        PartialSources,
        CheckFailed
    }

    /// <summary>
    /// View model for the game details window. Combines release data from the
    /// selected torrent/catalog candidate with official Steam Store metadata.
    /// Steam data is loaded in the background and never blocks the window.
    /// </summary>
    public class GameDetailsViewModel : ViewModelBase, IDisposable
    {
        private readonly GameCardViewModel _game;
        private readonly IGameSearchAggregator _searchAggregator;
        private readonly ISteamMetadataService? _steamMetadataService;
        private readonly string _installPath;
        private readonly DispatcherTimer _artworkTimer;
        private readonly DispatcherTimer _notificationTimer;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly string? _catalogDescription;

        private GameReleaseCandidate? _candidate;
        private VersionSelectionResult? _versionSelectionResult;
        private SteamGameMetadata? _steamMetadata;
        private SteamLoadState _steamState = SteamLoadState.Idle;
        private bool _isCheckingVersions = true;
        private bool _isRefreshing;
        private VersionLookupStatusType _lookupStatusType = VersionLookupStatusType.Checking;
        private string _lookupStatusTextKey = "GameDetailsCheckingVersions";
        private GameDetailsTab _selectedTab = GameDetailsTab.Overview;
        private string? _notificationMessage;
        private bool _isDisposed;

        private enum SteamLoadState
        {
            Idle,
            Loading,
            Loaded,
            Unavailable,
            Ambiguous
        }

        public GameDetailsViewModel(
            GameCardViewModel game,
            IGameSearchAggregator searchAggregator,
            string installPath,
            ISteamMetadataService? steamMetadataService = null)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _searchAggregator = searchAggregator ?? throw new ArgumentNullException(nameof(searchAggregator));
            _steamMetadataService = steamMetadataService;
            _installPath = string.IsNullOrWhiteSpace(installPath) ? @"C:\Games" : installPath;
            _candidate = _game.ReleaseCandidate;
            _catalogDescription = !string.IsNullOrWhiteSpace(_game.Description) && !IsCandidateGeneratedDescription(_game.Description)
                ? _game.Description
                : null;

            _game.PropertyChanged += OnGamePropertyChanged;
            LocalizationManager.LanguageChanged += OnLanguageChanged;

            _artworkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _artworkTimer.Tick += ArtworkTimer_Tick;

            _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _notificationTimer.Tick += (_, _) =>
            {
                _notificationTimer.Stop();
                NotificationMessage = null;
            };

            SelectOverviewTabCommand = new RelayCommand(() => SelectedTab = GameDetailsTab.Overview);
            SelectSpecsTabCommand = new RelayCommand(() => SelectedTab = GameDetailsTab.Specs);
            SelectStorageTabCommand = new RelayCommand(() => SelectedTab = GameDetailsTab.Storage);

            CopyTitleCommand = new RelayCommand(CopyTitle);
            CopySummaryCommand = new RelayCommand(CopySummary);
            CopyInstallPathCommand = new RelayCommand(CopyInstallPath, () => IsInstalled && !string.IsNullOrWhiteSpace(InstallPathDisplay));
            OpenFolderCommand = new RelayCommand(OpenFolder, () => CanOpenFolder);
            OpenSteamCommand = new RelayCommand(OpenSteam, () => CanOpenSteam);
            CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
            PrimaryActionCommand = new RelayCommand(ExecutePrimaryAction, () => CanExecutePrimaryAction);
            RefreshCommand = new AsyncRelayCommand(RefreshAllAsync, () => !_isRefreshing);

            StartActiveArtworkLoading();
            _ = InitializeAsync(forceSteamRefresh: false);
        }

        public event EventHandler? CloseRequested;

        public GameCardViewModel Game => _game;

        public RelayCommand SelectOverviewTabCommand { get; }
        public RelayCommand SelectSpecsTabCommand { get; }
        public RelayCommand SelectStorageTabCommand { get; }
        public RelayCommand CopyTitleCommand { get; }
        public RelayCommand CopySummaryCommand { get; }
        public RelayCommand CopyInstallPathCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand OpenSteamCommand { get; }
        public RelayCommand CloseCommand { get; }
        public RelayCommand PrimaryActionCommand { get; }
        public AsyncRelayCommand RefreshCommand { get; }

        public AsyncRelayCommand VerifyCommand => _game.VerifyCommand;
        public AsyncRelayCommand UpdateCommand => _game.UpdateCommand;
        public AsyncRelayCommand InstallCommand => _game.InstallCommand;

        // ── Tabs ──────────────────────────────────────────────────────────────
        public GameDetailsTab SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                {
                    OnPropertyChanged(nameof(IsOverviewTabSelected));
                    OnPropertyChanged(nameof(IsSpecsTabSelected));
                    OnPropertyChanged(nameof(IsStorageTabSelected));
                }
            }
        }

        public bool IsOverviewTabSelected => SelectedTab == GameDetailsTab.Overview;
        public bool IsSpecsTabSelected => SelectedTab == GameDetailsTab.Specs;
        public bool IsStorageTabSelected => SelectedTab == GameDetailsTab.Storage;

        // ── Notifications ─────────────────────────────────────────────────────
        public string? NotificationMessage
        {
            get => _notificationMessage;
            private set
            {
                if (SetProperty(ref _notificationMessage, value))
                    OnPropertyChanged(nameof(HasNotificationMessage));
            }
        }

        public bool HasNotificationMessage => !string.IsNullOrWhiteSpace(NotificationMessage);

        // ── Hero ──────────────────────────────────────────────────────────────
        public string Name => _game.Name;

        public string ShortDescription
        {
            get
            {
                var steam = _steamMetadata?.ShortDescription;
                if (!string.IsNullOrWhiteSpace(steam))
                    return SanitizePlainText(steam);

                var fallback = CleanDescription;
                if (string.IsNullOrWhiteSpace(fallback)
                    || string.Equals(fallback, LocalizationManager.GetString("GameDetailsNoDescription"), StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return Truncate(fallback.Replace("\r", " ").Replace("\n", " "), 230);
            }
        }

        public bool HasShortDescription => !string.IsNullOrWhiteSpace(ShortDescription);

        public string ReleaseDateDisplay
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_steamMetadata?.ReleaseDateText))
                    return _steamMetadata!.ReleaseDateText;

                var catalogRelease = _game.Definition.ReleaseDate;
                if (!string.IsNullOrWhiteSpace(catalogRelease))
                {
                    if (DateTime.TryParse(catalogRelease, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
                        || DateTime.TryParse(catalogRelease, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    {
                        return parsed.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
                    }
                    return catalogRelease;
                }

                // Release cards do not have a catalog date; the year from the raw
                // title is still a truthful, useful release hint.
                var rawTitle = (_candidate ?? _game.ReleaseCandidate)?.RawTitle;
                if (!string.IsNullOrWhiteSpace(rawTitle))
                {
                    var yearMatch = Regex.Match(rawTitle, @"\((19[5-9]\d|20\d\d)\)");
                    if (yearMatch.Success)
                        return yearMatch.Groups[1].Value;
                }

                return string.Empty;
            }
        }

        public bool HasReleaseDate => !string.IsNullOrWhiteSpace(ReleaseDateDisplay);

        public string ReleaseDateOrDash => HasReleaseDate ? ReleaseDateDisplay : "—";

        public IReadOnlyList<string> Genres
        {
            get
            {
                if (_steamMetadata != null && _steamMetadata.Genres.Count > 0)
                    return _steamMetadata.Genres;

                var fallback = new List<string>();
                var candidate = _candidate ?? _game.ReleaseCandidate;
                var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Новинки", "Каталог Vellum", "Vellum Catalog",
                    "Portable", "Repack", "Installer", "Archive", "Unknown",
                    "Portable + Repack", "Repack + Portable"
                };
                if (candidate != null)
                {
                    ignored.Add(candidate.SourceName);
                    ignored.Add(candidate.VersionType.ToString());
                }

                foreach (var category in _game.Definition.Categories)
                {
                    if (string.IsNullOrWhiteSpace(category) || ignored.Contains(category))
                        continue;
                    var localized = category switch
                    {
                        "CategoryNewReleases" => LocalizationManager.GetString("CategoryNewReleases"),
                        _ => category
                    };
                    if (!fallback.Contains(localized, StringComparer.OrdinalIgnoreCase))
                        fallback.Add(localized);
                }

                return fallback;
            }
        }

        public bool HasGenres => Genres.Count > 0;

        public string HeroGenresDisplay => string.Join(" • ", Genres.Take(3));

        public bool HasHeroGenres => Genres.Count > 0;

        public string HeroMetaDisplay
        {
            get
            {
                var parts = new List<string>();
                if (HasReleaseDate)
                    parts.Add(ReleaseDateDisplay);
                if (HasHeroGenres)
                    parts.Add(HeroGenresDisplay);
                if (!string.IsNullOrWhiteSpace(VersionDisplay) && VersionDisplay != "—")
                    parts.Add("v" + VersionDisplay.TrimStart('v', 'V'));
                return string.Join("  •  ", parts);
            }
        }

        public bool HasHeroMeta => !string.IsNullOrWhiteSpace(HeroMetaDisplay);

        public string SourceDisplay
        {
            get
            {
                var candidate = _candidate ?? _game.ReleaseCandidate;
                if (candidate != null && !string.IsNullOrWhiteSpace(candidate.SourceName))
                    return GetLocalizedSourceName(candidate);

                return "—";
            }
        }

        public bool HasSource => !string.IsNullOrWhiteSpace(SourceDisplay) && SourceDisplay != "—";

        public string DeveloperDisplay
        {
            get
            {
                if (_steamMetadata != null && _steamMetadata.Developers.Count > 0)
                    return string.Join(", ", _steamMetadata.Developers);

                if (!IsReleaseDerived && !string.IsNullOrWhiteSpace(_game.Definition.Developer))
                    return _game.Definition.Developer;

                return "—";
            }
        }

        public string PublisherDisplay
        {
            get
            {
                if (_steamMetadata != null && _steamMetadata.Publishers.Count > 0)
                    return string.Join(", ", _steamMetadata.Publishers);

                if (!IsReleaseDerived && !string.IsNullOrWhiteSpace(_game.Definition.Publisher))
                    return _game.Definition.Publisher;

                return "—";
            }
        }

        /// <summary>
        /// True when the card was created from a torrent release. In that case
        /// <see cref="GameCardViewModel.Definition"/>.Developer holds the repack
        /// author and Publisher holds the tracker name — never official metadata.
        /// </summary>
        public bool IsReleaseDerived
            => _game.ReleaseCandidate != null || IsCandidateGeneratedDescription(_game.Description);

        public string ReleaseAuthorDisplay
        {
            get
            {
                var author = (_candidate ?? _game.ReleaseCandidate)?.ReleaseAuthor;
                return !string.IsNullOrWhiteSpace(author) ? author : "—";
            }
        }

        public string BuildTypeDisplay
        {
            get
            {
                var (hasPortable, hasRepack) = ComputeBuildAvailability();
                if (hasPortable && hasRepack)
                    return "Repack + Portable";
                if (hasPortable)
                    return "Portable";
                if (hasRepack)
                    return "Repack";

                var candidate = _candidate ?? _game.ReleaseCandidate;
                return candidate?.VersionType switch
                {
                    GameVersionType.Installer => LocalizationManager.GetString("InstallVersionInstaller"),
                    GameVersionType.Archive => LocalizationManager.GetString("InstallVersionArchive"),
                    _ => LocalizationManager.GetString("InstallVersionUnknown")
                };
            }
        }

        public bool HasBuildType
        {
            get
            {
                var (hasPortable, hasRepack) = ComputeBuildAvailability();
                return hasPortable || hasRepack;
            }
        }

        public string BuildTypeSubtext
        {
            get
            {
                var (hasPortable, hasRepack) = ComputeBuildAvailability();
                if (hasPortable && hasRepack)
                    return LocalizationManager.GetString("GameDetailsBuildBoth");
                if (hasPortable)
                    return LocalizationManager.GetString("GameDetailsSubPortable");
                if (hasRepack)
                    return LocalizationManager.GetString("GameDetailsSubRepack");

                var candidate = _candidate ?? _game.ReleaseCandidate;
                return candidate?.VersionType switch
                {
                    GameVersionType.Installer => LocalizationManager.GetString("GameDetailsSubInstaller"),
                    GameVersionType.Archive => LocalizationManager.GetString("GameDetailsSubArchive"),
                    _ => "—"
                };
            }
        }

        public string VersionDisplay
        {
            get
            {
                var candidate = _candidate ?? _game.ReleaseCandidate;
                if (!string.IsNullOrWhiteSpace(candidate?.Version))
                    return candidate!.Version;
                return !string.IsNullOrWhiteSpace(_game.Version) ? _game.Version : "—";
            }
        }

        public string HeroImageUrl => _game.FeaturedImageUrl;
        public string CoverUrl => _game.CoverUrl;
        public string BannerUrl => _game.BannerUrl;
        public bool HasHeroImage => !string.IsNullOrWhiteSpace(HeroImageUrl);

        // ── Steam metadata ────────────────────────────────────────────────────
        public bool IsSteamLoading => _steamState == SteamLoadState.Loading;

        public bool HasSteamMetadata => _steamMetadata != null;

        public bool ShowSteamUnavailable => !IsSteamLoading && _steamMetadata == null;

        public string SteamStatusText
        {
            get
            {
                if (_steamState == SteamLoadState.Loading)
                    return LocalizationManager.GetString("GameDetailsSteamLoading");
                if (_steamMetadata != null)
                {
                    var updated = _steamMetadata.FetchedAtUtc == default
                        ? string.Empty
                        : _steamMetadata.FetchedAtUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
                    return string.IsNullOrWhiteSpace(updated)
                        ? LocalizationManager.GetString("GameDetailsSteamLoaded")
                        : LocalizationManager.GetString("GameDetailsSteamUpdated", updated);
                }
                if (_steamState == SteamLoadState.Ambiguous)
                    return LocalizationManager.GetString("GameDetailsSteamAmbiguous");
                if (_steamState == SteamLoadState.Unavailable)
                    return LocalizationManager.GetString("GameDetailsSteamUnavailable");
                return string.Empty;
            }
        }

        public string SteamUnavailableTitle => LocalizationManager.GetString("GameDetailsSteamUnavailableTitle");

        public string SteamUnavailableText => _steamState == SteamLoadState.Ambiguous
            ? LocalizationManager.GetString("GameDetailsSteamAmbiguousHint")
            : LocalizationManager.GetString("GameDetailsSteamUnavailableHint");

        public string SteamShortDescription => SanitizePlainText(_steamMetadata?.ShortDescription);

        public string SteamDescription
        {
            get
            {
                if (_steamMetadata == null)
                    return string.Empty;

                var detailed = SanitizeRichText(_steamMetadata.DetailedDescription);
                if (!string.IsNullOrWhiteSpace(detailed))
                    return detailed;

                return SanitizeRichText(_steamMetadata.ShortDescription);
            }
        }

        public bool HasSteamDescription => !string.IsNullOrWhiteSpace(SteamDescription);

        public bool IsEnglishDescriptionFallback => _steamMetadata?.IsEnglishDescriptionFallback == true;

        public string SteamReleaseDateDisplay => _steamMetadata?.ReleaseDateText ?? string.Empty;

        public string SteamDeveloperDisplay => _steamMetadata != null && _steamMetadata.Developers.Count > 0
            ? string.Join(", ", _steamMetadata.Developers)
            : string.Empty;

        public string SteamPublisherDisplay => _steamMetadata != null && _steamMetadata.Publishers.Count > 0
            ? string.Join(", ", _steamMetadata.Publishers)
            : string.Empty;

        public string SteamGenresDisplay => _steamMetadata != null && _steamMetadata.Genres.Count > 0
            ? string.Join(" • ", _steamMetadata.Genres)
            : string.Empty;

        public string SteamPlatformsDisplay
        {
            get
            {
                if (_steamMetadata == null || _steamMetadata.Platforms.Count == 0)
                    return string.Empty;
                return string.Join(" • ", _steamMetadata.Platforms.Select(platform => platform switch
                {
                    "windows" => "Windows",
                    "mac" => "macOS",
                    "linux" => "Linux",
                    _ => platform
                }));
            }
        }

        public string SteamLanguagesDisplay
        {
            get
            {
                var source = _steamMetadata?.SupportedLanguages;
                if (string.IsNullOrWhiteSpace(source))
                    return string.Empty;

                return string.Join(Environment.NewLine, source
                    .Replace("*", string.Empty, StringComparison.Ordinal)
                    .Split(new[] { ',', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !line.Contains("доступно на этих языках", StringComparison.OrdinalIgnoreCase)
                                   && !line.Contains("available on these languages", StringComparison.OrdinalIgnoreCase)
                                   && !line.Contains("available in these languages", StringComparison.OrdinalIgnoreCase)));
            }
        }

        public string SteamMetacriticDisplay => _steamMetadata?.MetacriticScore ?? string.Empty;

        public bool HasSteamMetacritic => !string.IsNullOrWhiteSpace(SteamMetacriticDisplay);

        public string SteamStoreUrl => _steamMetadata?.StoreUrl ?? string.Empty;

        public bool CanOpenSteam => !string.IsNullOrWhiteSpace(SteamStoreUrl)
                                    && Uri.TryCreate(SteamStoreUrl, UriKind.Absolute, out var uri)
                                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        public string MinimumRequirementsDisplay => _steamMetadata?.MinimumRequirements ?? string.Empty;

        public string RecommendedRequirementsDisplay => _steamMetadata?.RecommendedRequirements ?? string.Empty;

        public bool HasSteamRequirements => !string.IsNullOrWhiteSpace(MinimumRequirementsDisplay)
                                            || !string.IsNullOrWhiteSpace(RecommendedRequirementsDisplay);

        public bool HasMinimumRequirements => !string.IsNullOrWhiteSpace(MinimumRequirementsDisplay);
        public bool HasRecommendedRequirements => !string.IsNullOrWhiteSpace(RecommendedRequirementsDisplay);

        public bool HasSteamPlatforms => !string.IsNullOrWhiteSpace(SteamPlatformsDisplay);
        public bool HasSteamLanguages => !string.IsNullOrWhiteSpace(SteamLanguagesDisplay);
        public bool HasSteamDeveloper => !string.IsNullOrWhiteSpace(SteamDeveloperDisplay);
        public bool HasSteamPublisher => !string.IsNullOrWhiteSpace(SteamPublisherDisplay);
        public bool HasSteamGenres => !string.IsNullOrWhiteSpace(SteamGenresDisplay);

        // ── Description / overview ────────────────────────────────────────────
        public string CleanDescription
        {
            get
            {
                var description = !string.IsNullOrWhiteSpace(_catalogDescription)
                    ? _catalogDescription
                    : (!IsCandidateGeneratedDescription(_game.Description) ? _game.Description : string.Empty);
                return SanitizePlainText(description);
            }
        }

        public string RawDescription => _game.Description;

        public bool HasDescription => !string.IsNullOrWhiteSpace(CleanDescription)
                                      && !string.Equals(
                                          CleanDescription,
                                          LocalizationManager.GetString("GameDetailsNoDescription"),
                                          StringComparison.OrdinalIgnoreCase);

        /// <summary>Full description used by the Overview tab: Steam first, release text otherwise.</summary>
        public string FullDescription
        {
            get
            {
                if (HasSteamDescription)
                    return SteamDescription;
                return CleanDescription;
            }
        }

        public bool HasFullDescription => !string.IsNullOrWhiteSpace(FullDescription)
                                          && !string.Equals(
                                              FullDescription,
                                              LocalizationManager.GetString("GameDetailsNoDescription"),
                                              StringComparison.OrdinalIgnoreCase);

        public bool IsDescriptionFromSteam => HasSteamDescription;

        public string DescriptionSourceLabel => HasSteamDescription
            ? LocalizationManager.GetString("GameDetailsDescriptionSteam")
            : LocalizationManager.GetString("GameDetailsDescriptionRelease");

        // ── Sizes and specs ───────────────────────────────────────────────────
        public string DownloadSizeDisplay
        {
            get
            {
                var candidate = _candidate ?? _game.ReleaseCandidate;
                if (candidate != null && candidate.DownloadSizeBytes > 0)
                    return PathHelper.FormatBytes(candidate.DownloadSizeBytes);
                if (_game.Definition.DownloadSizeBytes > 0)
                    return PathHelper.FormatBytes(_game.Definition.DownloadSizeBytes);
                return _game.TotalDownloadSizeDisplay;
            }
        }

        public string InstalledSizeDisplay
        {
            get
            {
                if (_game.Installation.InstalledSizeBytes > 0)
                    return PathHelper.FormatBytes(_game.Installation.InstalledSizeBytes);
                var candidate = _candidate ?? _game.ReleaseCandidate;
                if (candidate != null && candidate.EstimatedInstalledSizeBytes > 0)
                    return PathHelper.FormatBytes(candidate.EstimatedInstalledSizeBytes);
                if (_game.Definition.SizeBytes > 0)
                    return PathHelper.FormatBytes(_game.Definition.SizeBytes);
                return _game.SizeDisplay;
            }
        }

        public string TotalDownloadSizeDisplay => DownloadSizeDisplay;
        public string SizeDisplay => InstalledSizeDisplay;

        public string ProtocolDisplay => _game.SourceProtocolDisplay;

        public string SeedersPeersDisplay
        {
            get
            {
                var candidate = _candidate ?? _game.ReleaseCandidate;
                if (candidate != null && (candidate.Seeders > 0 || candidate.Leechers > 0))
                    return $"{candidate.Seeders} / {candidate.Leechers}";
                return _game.PeersDisplay;
            }
        }

        public bool HasSeeders => !string.IsNullOrWhiteSpace(SeedersPeersDisplay) && SeedersPeersDisplay != "—";

        public string ReleaseTitleDisplay
        {
            get
            {
                var raw = (_candidate ?? _game.ReleaseCandidate)?.RawTitle;
                return !string.IsNullOrWhiteSpace(raw) ? raw! : string.Empty;
            }
        }

        public bool HasReleaseTitle => !string.IsNullOrWhiteSpace(ReleaseTitleDisplay)
                                       && !string.Equals(ReleaseTitleDisplay, Name, StringComparison.OrdinalIgnoreCase);

        public string ReleasePublishDateDisplay
        {
            get
            {
                var published = (_candidate ?? _game.ReleaseCandidate)?.PublishDate;
                return published?.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) ?? "—";
            }
        }

        public string ReleaseLanguageDisplay
        {
            get
            {
                var language = (_candidate ?? _game.ReleaseCandidate)?.Language;
                return !string.IsNullOrWhiteSpace(language) ? language! : "—";
            }
        }

        public string CandidateDetailsUrl => _candidate?.DetailsUrl ?? _game.ReleaseCandidate?.DetailsUrl ?? string.Empty;

        public bool HasDetailsUrl => !string.IsNullOrWhiteSpace(CandidateDetailsUrl)
                                     && Uri.TryCreate(CandidateDetailsUrl, UriKind.Absolute, out var uri)
                                     && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        public DiskSpaceRequirement? SpaceRequirement => _versionSelectionResult?.SpaceRequirement;

        public string RequiredDiskSpaceDisplay => SpaceRequirement != null
            ? PathHelper.FormatBytes(SpaceRequirement.RequiredFreeSpaceBytes)
            : InstalledSizeDisplay;

        public string AvailableDiskSpaceDisplay => SpaceRequirement != null
            ? PathHelper.FormatBytes(SpaceRequirement.AvailableFreeSpaceBytes)
            : "—";

        public bool HasDiskSpaceInfo => SpaceRequirement != null;
        public bool HasEnoughSpace => SpaceRequirement?.HasEnoughSpace ?? true;

        public string SpaceStatusDisplay => HasEnoughSpace
            ? LocalizationManager.GetString("GameDetailsSpaceEnough")
            : LocalizationManager.GetString("GameDetailsSpaceNotEnough");

        // ── Version lookup status ─────────────────────────────────────────────
        public bool IsCheckingVersions
        {
            get => _isCheckingVersions;
            private set => SetProperty(ref _isCheckingVersions, value);
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set => SetProperty(ref _isRefreshing, value);
        }

        public VersionLookupStatusType LookupStatusType
        {
            get => _lookupStatusType;
            private set => SetProperty(ref _lookupStatusType, value);
        }

        public string LookupStatusText => LocalizationManager.GetString(_lookupStatusTextKey);

        public bool ShowLookupWarning => LookupStatusType is VersionLookupStatusType.PartialSources
            or VersionLookupStatusType.CheckFailed;

        // ── Install state ─────────────────────────────────────────────────────
        public string StatusText => _game.StatusText;
        public InstallState State => _game.State;
        public bool IsInstalled => _game.IsInstalled;
        public bool IsRunning => _game.IsRunning;
        public bool IsDownloading => _game.IsDownloading;
        public bool IsPaused => _game.IsPaused;
        public bool IsError => _game.IsError;
        public bool IsBusy => _game.IsBusy;

        public bool ShowProgress => IsDownloading || IsPaused || IsError;
        public int ProgressPercent => (int)Math.Round(_game.ProgressPercent);
        public string ProgressText => _game.ProgressText;
        public string DownloadSpeedDisplay => _game.DownloadSpeedDisplay;
        public string RemainingTimeDisplay => _game.RemainingTimeDisplay;

        public string InstallPathDisplay => !string.IsNullOrWhiteSpace(_game.InstallPath)
            ? _game.InstallPath
            : LocalizationManager.GetString("GameDetailsNoInstallPath");

        public string LibraryPathDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_game.InstallPath))
                    return "—";
                try
                {
                    var parent = Directory.GetParent(_game.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return parent?.FullName ?? _game.InstallPath;
                }
                catch
                {
                    return _game.InstallPath;
                }
            }
        }

        public string InstalledVersionDisplay => !string.IsNullOrWhiteSpace(_game.InstalledVersion)
            ? _game.InstalledVersion
            : VersionDisplay;

        public string InstalledAtDisplay => _game.Installation.InstalledAt == default
            ? "—"
            : _game.Installation.InstalledAt.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

        public string LastUpdatedDisplay => _game.Installation.LastUpdatedAt == default
            ? "—"
            : _game.Installation.LastUpdatedAt.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

        public bool CanOpenFolder => IsInstalled && !string.IsNullOrWhiteSpace(_game.InstallPath) && Directory.Exists(_game.InstallPath);

        // ── Primary action ────────────────────────────────────────────────────
        public string PrimaryActionLabel
        {
            get
            {
                if (IsRunning)
                    return LocalizationManager.GetString("StatusRunning");
                if (IsInstalled)
                    return LocalizationManager.GetString("CardPlay");
                if (IsDownloading)
                    return $"{LocalizationManager.GetString("StatusDownloading")} ({ProgressPercent}%)";
                if (IsPaused)
                    return LocalizationManager.GetString("CardResume");
                if (IsError)
                    return LocalizationManager.GetString("CardRetry");
                return LocalizationManager.GetString("CardInstall");
            }
        }

        public bool CanExecutePrimaryAction
        {
            get
            {
                if (IsBusy || IsRunning)
                    return false;
                if (IsInstalled)
                    return _game.PlayCommand.CanExecute(null);
                if (IsDownloading)
                    return _game.PauseCommand.CanExecute(null);
                if (IsPaused)
                    return _game.ResumeCommand.CanExecute(null);
                if (IsError)
                    return _game.RetryDownloadCommand.CanExecute(null) || _game.InstallCommand.CanExecute(null);
                return _game.InstallCommand.CanExecute(null);
            }
        }

        public void ExecutePrimaryAction()
        {
            if (IsRunning || IsBusy)
                return;

            if (IsInstalled)
            {
                if (_game.PlayCommand.CanExecute(null))
                    _game.PlayCommand.Execute(null);
            }
            else if (IsDownloading)
            {
                if (_game.PauseCommand.CanExecute(null))
                    _game.PauseCommand.Execute(null);
            }
            else if (IsPaused)
            {
                if (_game.ResumeCommand.CanExecute(null))
                    _game.ResumeCommand.Execute(null);
            }
            else if (IsError)
            {
                if (_game.RetryDownloadCommand.CanExecute(null))
                    _game.RetryDownloadCommand.Execute(null);
                else if (_game.InstallCommand.CanExecute(null))
                    _game.InstallCommand.Execute(null);
            }
            else if (_game.InstallCommand.CanExecute(null))
            {
                _game.InstallCommand.Execute(null);
            }
        }

        // ── Actions ───────────────────────────────────────────────────────────
        public void CopyTitle()
        {
            try
            {
                Clipboard.SetText(Name);
                ShowNotification(LocalizationManager.GetString("GameDetailsCopiedTitle"));
            }
            catch
            {
                ShowNotification(LocalizationManager.GetString("GameDetailsCopyFailed"));
            }
        }

        public void CopyInstallPath()
        {
            try
            {
                Clipboard.SetText(InstallPathDisplay);
                ShowNotification(LocalizationManager.GetString("GameDetailsCopiedPath"));
            }
            catch
            {
                ShowNotification(LocalizationManager.GetString("GameDetailsCopyFailed"));
            }
        }

        public void CopySummary()
        {
            try
            {
                Clipboard.SetText(GenerateFormattedSummary());
                ShowNotification(LocalizationManager.GetString("GameDetailsCopiedAll"));
            }
            catch
            {
                ShowNotification(LocalizationManager.GetString("GameDetailsCopyFailed"));
            }
        }

        public void OpenFolder()
        {
            if (CanOpenFolder)
                _game.OpenFolderCommand.Execute(null);
        }

        public void OpenSteam()
        {
            if (!CanOpenSteam)
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SteamStoreUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open Steam page: {ex.Message}");
            }
        }

        private string GenerateFormattedSummary()
        {
            var builder = new StringBuilder();
            builder.AppendLine("═════════════════════════════════════════════════════════════");
            builder.AppendLine($"🎮  {Name}");
            builder.AppendLine("═════════════════════════════════════════════════════════════");
            builder.AppendLine($"• {LocalizationManager.GetString("DetailType")}: {BuildTypeDisplay}");
            builder.AppendLine($"• {LocalizationManager.GetString("DetailVersion")}: {VersionDisplay}");
            builder.AppendLine($"• {LocalizationManager.GetString("DetailStatus")}: {StatusText}");
            builder.AppendLine($"• {LocalizationManager.GetString("DetailDownloadSize")}: {DownloadSizeDisplay}");
            builder.AppendLine($"• {LocalizationManager.GetString("DetailInstallSize")}: {InstalledSizeDisplay}");

            if (!string.IsNullOrWhiteSpace(DeveloperDisplay) && DeveloperDisplay != "—")
                builder.AppendLine($"• {LocalizationManager.GetString("DetailDeveloper")}: {DeveloperDisplay}");
            if (!string.IsNullOrWhiteSpace(PublisherDisplay) && PublisherDisplay != "—")
                builder.AppendLine($"• {LocalizationManager.GetString("DetailPublisher")}: {PublisherDisplay}");
            if (!string.IsNullOrWhiteSpace(SourceDisplay) && SourceDisplay != "—")
                builder.AppendLine($"• {LocalizationManager.GetString("DetailSource")}: {SourceDisplay}");
            if (!string.IsNullOrWhiteSpace(ReleaseAuthorDisplay) && ReleaseAuthorDisplay != "—")
                builder.AppendLine($"• {LocalizationManager.GetString("GameDetailsReleaseAuthor")}: {ReleaseAuthorDisplay}");
            if (!string.IsNullOrWhiteSpace(ReleaseDateDisplay))
                builder.AppendLine($"• {LocalizationManager.GetString("GameDetailsReleaseDate")}: {ReleaseDateDisplay}");
            if (!string.IsNullOrWhiteSpace(SteamGenresDisplay))
                builder.AppendLine($"• {LocalizationManager.GetString("DetailCategories")}: {SteamGenresDisplay}");
            if (CanOpenSteam)
                builder.AppendLine($"• Steam: {SteamStoreUrl}");

            if (HasReleaseTitle)
                builder.AppendLine($"• {LocalizationManager.GetString("DetailOriginalName")}: {ReleaseTitleDisplay}");

            if (HasFullDescription)
            {
                builder.AppendLine();
                builder.AppendLine($"── [ {LocalizationManager.GetString("DetailDescription")} ] ──");
                builder.AppendLine(HasFullDescription ? FullDescription : SteamShortDescription);
            }

            builder.AppendLine();
            builder.AppendLine($"Vellum Launcher • {DateTime.Now:yyyy-MM-dd HH:mm}");
            return builder.ToString();
        }

        private void ShowNotification(string message)
        {
            NotificationMessage = message;
            _notificationTimer.Stop();
            _notificationTimer.Start();
        }

        // ── Data loading ──────────────────────────────────────────────────────
        private async Task InitializeAsync(bool forceSteamRefresh)
        {
            await ResolvePreferredVersionAsync();
            await LoadSteamMetadataAsync(forceSteamRefresh);
        }

        private async Task RefreshAllAsync()
        {
            if (_isDisposed || IsRefreshing)
                return;

            IsRefreshing = true;
            RefreshCommand.RaiseCanExecuteChanged();
            try
            {
                await ResolvePreferredVersionAsync();
                await LoadSteamMetadataAsync(forceRefresh: true);
                ShowNotification(LocalizationManager.GetString("GameDetailsRefreshed"));
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                ShowNotification(LocalizationManager.GetString("GameDetailsRefreshFailed"));
            }
            finally
            {
                IsRefreshing = false;
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task ResolvePreferredVersionAsync()
        {
            if (_isDisposed)
                return;

            IsCheckingVersions = true;
            LookupStatusType = VersionLookupStatusType.Checking;
            _lookupStatusTextKey = "GameDetailsCheckingVersions";
            OnPropertyChanged(nameof(LookupStatusText));
            OnPropertyChanged(nameof(ShowLookupWarning));

            try
            {
                var result = await _searchAggregator.FindAndSelectBestVersionAsync(
                    _game.Name,
                    _installPath,
                    _lifetimeCts.Token).ConfigureAwait(true);

                if (_isDisposed)
                    return;

                _versionSelectionResult = result;
                if (result.SelectedCandidate != null)
                    _candidate = result.SelectedCandidate;

                if (_candidate?.VersionType == GameVersionType.Portable)
                {
                    if (string.Equals(_candidate.SourceId, "rutor", StringComparison.OrdinalIgnoreCase))
                        _game.ApplyPreferredRelease(_candidate);

                    LookupStatusType = VersionLookupStatusType.PortableFound;
                    _lookupStatusTextKey = "GameDetailsPortableFound";
                }
                else if (_candidate != null)
                {
                    LookupStatusType = VersionLookupStatusType.RepackOnly;
                    _lookupStatusTextKey = result.Errors.Count > 0
                        ? "GameDetailsSourcesPartial"
                        : "GameDetailsPortableMissing";
                }
                else
                {
                    LookupStatusType = VersionLookupStatusType.CheckFailed;
                    _lookupStatusTextKey = "GameDetailsCheckFailed";
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                LookupStatusType = VersionLookupStatusType.CheckFailed;
                _lookupStatusTextKey = "GameDetailsCheckFailed";
            }
            finally
            {
                IsCheckingVersions = false;
                RefreshAllProperties();
            }
        }

        private async Task LoadSteamMetadataAsync(bool forceRefresh)
        {
            if (_isDisposed)
                return;

            if (_steamMetadataService == null)
            {
                _steamMetadata = null;
                _steamState = SteamLoadState.Unavailable;
                RaiseSteamProperties();
                return;
            }

            _steamState = SteamLoadState.Loading;
            RaiseSteamProperties();

            try
            {
                var request = BuildSteamRequest();
                var result = await _steamMetadataService
                    .GetMetadataAsync(request, forceRefresh, _lifetimeCts.Token)
                    .ConfigureAwait(true);

                if (_isDisposed)
                    return;

                if (result.Metadata != null)
                {
                    _steamMetadata = result.Metadata;
                    _steamState = SteamLoadState.Loaded;
                }
                else
                {
                    _steamMetadata = null;
                    _steamState = result.IsAmbiguous ? SteamLoadState.Ambiguous : SteamLoadState.Unavailable;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                _steamMetadata = null;
                _steamState = SteamLoadState.Unavailable;
            }
            finally
            {
                if (!_isDisposed)
                {
                    RaiseSteamProperties();
                    RefreshAllProperties();
                }
            }
        }

        private SteamMetadataRequest BuildSteamRequest()
        {
            var candidate = _candidate ?? _game.ReleaseCandidate;
            var title = !string.IsNullOrWhiteSpace(candidate?.CleanTitle)
                ? candidate!.CleanTitle
                : _game.Name;

            return new SteamMetadataRequest
            {
                Title = title,
                AlternateTitle = candidate?.AltTitle,
                RawTitle = candidate?.RawTitle ?? _game.Definition.Name,
                AppId = candidate?.SteamAppId ?? _game.Definition.SteamAppId,
                ReleaseYear = ResolveCatalogReleaseYear(_game.Definition.ReleaseDate),
                LanguageCode = LocalizationManager.CurrentLanguage
            };
        }

        private static int? ResolveCatalogReleaseYear(string? releaseDate)
        {
            if (string.IsNullOrWhiteSpace(releaseDate))
                return null;

            if (DateTime.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || DateTime.TryParse(releaseDate, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
            {
                return parsed.Year;
            }

            var match = Regex.Match(releaseDate, @"\b(19[5-9]\d|20\d\d)\b");
            return match.Success && int.TryParse(match.Value, out var year) ? year : null;
        }

        private (bool HasPortable, bool HasRepack) ComputeBuildAvailability()
        {
            var candidates = new List<GameReleaseCandidate>();
            if (_versionSelectionResult?.AllCandidates != null)
                candidates.AddRange(_versionSelectionResult.AllCandidates);
            if (_game.ReleaseCandidate != null)
                candidates.Add(_game.ReleaseCandidate);
            if (_candidate != null)
                candidates.Add(_candidate);

            var hasPortable = candidates.Any(c => c.VersionType == GameVersionType.Portable);
            var hasRepack = candidates.Any(c => c.VersionType == GameVersionType.Repack);
            return (hasPortable, hasRepack);
        }

        private void StartActiveArtworkLoading()
        {
            _ = _game.EnsureArtworkLoadedAsync(forceRefresh: true, requireBanner: true, priority: ArtworkPriority.ModalDetails);
            if (!_game.HasCoverArtwork || !_game.HasBannerArtwork)
                _artworkTimer.Start();
        }

        private async void ArtworkTimer_Tick(object? sender, EventArgs e)
        {
            if (_isDisposed)
            {
                _artworkTimer.Stop();
                return;
            }

            if (_game.HasCoverArtwork && _game.HasBannerArtwork)
            {
                _artworkTimer.Stop();
                return;
            }

            try
            {
                await _game.EnsureArtworkLoadedAsync(forceRefresh: false, requireBanner: true, priority: ArtworkPriority.ModalDetails);
            }
            catch
            {
            }

            if (_game.HasCoverArtwork && _game.HasBannerArtwork)
                _artworkTimer.Stop();
        }

        // ── Change handling ───────────────────────────────────────────────────
        private void OnGamePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isDisposed)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed)
                    return;

                switch (e.PropertyName)
                {
                    case nameof(GameCardViewModel.CoverUrl):
                    case nameof(GameCardViewModel.BannerUrl):
                    case nameof(GameCardViewModel.FeaturedImageUrl):
                    case nameof(GameCardViewModel.HasBannerArtwork):
                    case nameof(GameCardViewModel.HasCoverArtwork):
                        OnPropertyChanged(nameof(CoverUrl));
                        OnPropertyChanged(nameof(BannerUrl));
                        OnPropertyChanged(nameof(HeroImageUrl));
                        OnPropertyChanged(nameof(HasHeroImage));
                        break;

                    case nameof(GameCardViewModel.State):
                    case nameof(GameCardViewModel.StatusText):
                    case nameof(GameCardViewModel.IsInstalled):
                    case nameof(GameCardViewModel.IsRunning):
                    case nameof(GameCardViewModel.IsDownloading):
                    case nameof(GameCardViewModel.IsPaused):
                    case nameof(GameCardViewModel.IsError):
                    case nameof(GameCardViewModel.IsBusy):
                        OnPropertyChanged(nameof(State));
                        OnPropertyChanged(nameof(StatusText));
                        OnPropertyChanged(nameof(IsInstalled));
                        OnPropertyChanged(nameof(IsRunning));
                        OnPropertyChanged(nameof(IsDownloading));
                        OnPropertyChanged(nameof(IsPaused));
                        OnPropertyChanged(nameof(IsError));
                        OnPropertyChanged(nameof(IsBusy));
                        OnPropertyChanged(nameof(ShowProgress));
                        OnPropertyChanged(nameof(PrimaryActionLabel));
                        OnPropertyChanged(nameof(CanExecutePrimaryAction));
                        OnPropertyChanged(nameof(CanOpenFolder));
                        OnPropertyChanged(nameof(InstallPathDisplay));
                        OnPropertyChanged(nameof(LibraryPathDisplay));
                        OnPropertyChanged(nameof(InstalledVersionDisplay));
                        OnPropertyChanged(nameof(InstalledAtDisplay));
                        OnPropertyChanged(nameof(LastUpdatedDisplay));
                        PrimaryActionCommand.RaiseCanExecuteChanged();
                        OpenFolderCommand.RaiseCanExecuteChanged();
                        CopyInstallPathCommand.RaiseCanExecuteChanged();
                        break;

                    case nameof(GameCardViewModel.ProgressPercent):
                    case nameof(GameCardViewModel.ProgressText):
                    case nameof(GameCardViewModel.DownloadSpeedDisplay):
                    case nameof(GameCardViewModel.RemainingTimeDisplay):
                    case nameof(GameCardViewModel.PeersDisplay):
                        OnPropertyChanged(nameof(ProgressPercent));
                        OnPropertyChanged(nameof(ProgressText));
                        OnPropertyChanged(nameof(DownloadSpeedDisplay));
                        OnPropertyChanged(nameof(RemainingTimeDisplay));
                        OnPropertyChanged(nameof(SeedersPeersDisplay));
                        OnPropertyChanged(nameof(PrimaryActionLabel));
                        break;

                    case nameof(GameCardViewModel.ReleaseCandidate):
                        _candidate = _game.ReleaseCandidate;
                        RefreshAllProperties();
                        break;

                    case nameof(GameCardViewModel.Description):
                        OnPropertyChanged(nameof(RawDescription));
                        OnPropertyChanged(nameof(CleanDescription));
                        OnPropertyChanged(nameof(HasDescription));
                        OnPropertyChanged(nameof(ShortDescription));
                        OnPropertyChanged(nameof(HasShortDescription));
                        OnPropertyChanged(nameof(FullDescription));
                        OnPropertyChanged(nameof(HasFullDescription));
                        break;
                }
            });
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (_isDisposed)
                return;

            RefreshAllProperties();
            // The existing translation is cached per language, so this either answers
            // immediately or performs one background request.
            _ = LoadSteamMetadataAsync(forceRefresh: false);
        }

        private void RaiseSteamProperties()
        {
            OnPropertyChanged(nameof(IsSteamLoading));
            OnPropertyChanged(nameof(HasSteamMetadata));
            OnPropertyChanged(nameof(ShowSteamUnavailable));
            OnPropertyChanged(nameof(SteamStatusText));
            OnPropertyChanged(nameof(SteamUnavailableTitle));
            OnPropertyChanged(nameof(SteamUnavailableText));
            OnPropertyChanged(nameof(SteamDescription));
            OnPropertyChanged(nameof(HasSteamDescription));
            OnPropertyChanged(nameof(IsEnglishDescriptionFallback));
            OnPropertyChanged(nameof(ShortDescription));
            OnPropertyChanged(nameof(HasShortDescription));
        }

        private void RefreshAllProperties()
        {
            OnPropertyChanged(nameof(LookupStatusText));
            OnPropertyChanged(nameof(ShowLookupWarning));
            OnPropertyChanged(nameof(BuildTypeDisplay));
            OnPropertyChanged(nameof(HasBuildType));
            OnPropertyChanged(nameof(BuildTypeSubtext));
            OnPropertyChanged(nameof(VersionDisplay));
            OnPropertyChanged(nameof(DeveloperDisplay));
            OnPropertyChanged(nameof(PublisherDisplay));
            OnPropertyChanged(nameof(SourceDisplay));
            OnPropertyChanged(nameof(HasSource));
            OnPropertyChanged(nameof(ReleaseAuthorDisplay));
            OnPropertyChanged(nameof(Genres));
            OnPropertyChanged(nameof(HasGenres));
            OnPropertyChanged(nameof(HeroGenresDisplay));
            OnPropertyChanged(nameof(HasHeroGenres));
            OnPropertyChanged(nameof(HeroMetaDisplay));
            OnPropertyChanged(nameof(HasHeroMeta));
            OnPropertyChanged(nameof(ReleaseDateDisplay));
            OnPropertyChanged(nameof(HasReleaseDate));
            OnPropertyChanged(nameof(ReleaseDateOrDash));
            OnPropertyChanged(nameof(DownloadSizeDisplay));
            OnPropertyChanged(nameof(InstalledSizeDisplay));
            OnPropertyChanged(nameof(TotalDownloadSizeDisplay));
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(SeedersPeersDisplay));
            OnPropertyChanged(nameof(HasSeeders));
            OnPropertyChanged(nameof(InstallPathDisplay));
            OnPropertyChanged(nameof(LibraryPathDisplay));
            OnPropertyChanged(nameof(InstalledVersionDisplay));
            OnPropertyChanged(nameof(InstalledAtDisplay));
            OnPropertyChanged(nameof(LastUpdatedDisplay));
            OnPropertyChanged(nameof(CanOpenFolder));
            OnPropertyChanged(nameof(ReleaseTitleDisplay));
            OnPropertyChanged(nameof(HasReleaseTitle));
            OnPropertyChanged(nameof(ReleasePublishDateDisplay));
            OnPropertyChanged(nameof(ReleaseLanguageDisplay));
            OnPropertyChanged(nameof(CandidateDetailsUrl));
            OnPropertyChanged(nameof(HasDetailsUrl));
            OnPropertyChanged(nameof(SpaceRequirement));
            OnPropertyChanged(nameof(RequiredDiskSpaceDisplay));
            OnPropertyChanged(nameof(AvailableDiskSpaceDisplay));
            OnPropertyChanged(nameof(HasDiskSpaceInfo));
            OnPropertyChanged(nameof(HasEnoughSpace));
            OnPropertyChanged(nameof(SpaceStatusDisplay));
            OnPropertyChanged(nameof(ProtocolDisplay));
            OnPropertyChanged(nameof(PrimaryActionLabel));
            OnPropertyChanged(nameof(CanExecutePrimaryAction));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(ShowProgress));
            OnPropertyChanged(nameof(CleanDescription));
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(ShortDescription));
            OnPropertyChanged(nameof(HasShortDescription));
            OnPropertyChanged(nameof(FullDescription));
            OnPropertyChanged(nameof(HasFullDescription));
            OnPropertyChanged(nameof(IsDescriptionFromSteam));
            OnPropertyChanged(nameof(DescriptionSourceLabel));
            OnPropertyChanged(nameof(SteamReleaseDateDisplay));
            OnPropertyChanged(nameof(SteamDeveloperDisplay));
            OnPropertyChanged(nameof(HasSteamDeveloper));
            OnPropertyChanged(nameof(SteamPublisherDisplay));
            OnPropertyChanged(nameof(HasSteamPublisher));
            OnPropertyChanged(nameof(SteamGenresDisplay));
            OnPropertyChanged(nameof(HasSteamGenres));
            OnPropertyChanged(nameof(SteamPlatformsDisplay));
            OnPropertyChanged(nameof(HasSteamPlatforms));
            OnPropertyChanged(nameof(SteamLanguagesDisplay));
            OnPropertyChanged(nameof(HasSteamLanguages));
            OnPropertyChanged(nameof(SteamMetacriticDisplay));
            OnPropertyChanged(nameof(HasSteamMetacritic));
            OnPropertyChanged(nameof(SteamStoreUrl));
            OnPropertyChanged(nameof(CanOpenSteam));
            OnPropertyChanged(nameof(MinimumRequirementsDisplay));
            OnPropertyChanged(nameof(RecommendedRequirementsDisplay));
            OnPropertyChanged(nameof(HasSteamRequirements));
            OnPropertyChanged(nameof(HasMinimumRequirements));
            OnPropertyChanged(nameof(HasRecommendedRequirements));
            OpenSteamCommand.RaiseCanExecuteChanged();
            CopyInstallPathCommand.RaiseCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
            PrimaryActionCommand.RaiseCanExecuteChanged();
        }

        // ── Text helpers ──────────────────────────────────────────────────────
        public static bool IsCandidateGeneratedDescription(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return text.Contains("| Источник:", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("| Source:", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("| Джерело:", StringComparison.OrdinalIgnoreCase);
        }

        private static string Truncate(string text, int maxLength)
        {
            if (text.Length <= maxLength)
                return text;

            var cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
            if (cut < maxLength / 2)
                cut = maxLength;
            return text.Substring(0, cut).TrimEnd(' ', ',', '.', ';', ':') + "…";
        }

        private static string SanitizePlainText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return LocalizationManager.GetString("GameDetailsNoDescription");

            var text = SanitizeRichText(input);
            return string.IsNullOrWhiteSpace(text)
                ? LocalizationManager.GetString("GameDetailsNoDescription")
                : text;
        }

        private static string SanitizeRichText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var text = input;

            // Steam uses both HTML and BBCode depending on the field.
            text = Regex.Replace(text, @"\[img\].*?\[/img\]", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, @"\[/?(?:b|i|u|h1|h2|h3|list|\*|url[^\]]*|quote|code|spoiler)\]", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\[/?(?:b|i|u|h1|h2|h3|list|\*|quote|code|spoiler)[^\]]*\]", " ", RegexOptions.IgnoreCase);

            text = Regex.Replace(text, @"<(br|p|/p|div|/div|/li|h[1-6]|/h[1-6])\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", string.Empty);
            text = WebUtility.HtmlDecode(text);

            text = Regex.Replace(text, @"\r\n|\r", "\n");
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @" *\n *", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            text = text.Trim();

            return text;
        }

        private static string GetLocalizedSourceName(GameReleaseCandidate candidate)
            => string.Equals(candidate.SourceId, "catalog", StringComparison.OrdinalIgnoreCase)
                ? LocalizationManager.GetString("BuiltInCatalogName")
                : candidate.SourceName;

        public void Dispose()
        {
            if (_isDisposed)
                return;
            _isDisposed = true;

            try { _lifetimeCts.Cancel(); } catch { }
            _lifetimeCts.Dispose();
            _artworkTimer.Stop();
            _notificationTimer.Stop();
            _game.PropertyChanged -= OnGamePropertyChanged;
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
        }
    }
}
