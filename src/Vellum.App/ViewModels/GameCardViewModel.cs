using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Vellum.App.Views;
using Vellum.App.Services;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Microsoft.Win32;

namespace Vellum.App.ViewModels
{
    public class GameCardViewModel : ViewModelBase
    {
        private readonly IGameManager _gameManager;
        private readonly IGameLauncher _gameLauncher;
        private readonly ISettingsService _settingsService;
        private readonly IGameSearchAggregator _searchAggregator;
        private readonly IGameArtworkService _artworkService;
        private readonly NotificationService _notifications;

        private GameDefinition _definition;
        private GameInstallation _installation;
        private DownloadProgress? _progress;
        private CancellationTokenSource? _cts;
        private bool _isActionBusy;
        private bool _isRemovingDownload;
        private bool _isLibraryRecoveryEntry;
        private bool _hasAlternativeVersion;   // true when both Portable AND Repack are known to exist
        private string _coverUrl = string.Empty;
        private string _bannerUrl = string.Empty;
        // Artwork URLs that failed to decode (for example a Steam CDN banner that
        // returns 404). They are never applied to this card again, otherwise the
        // featured hero would keep switching between the broken banner and the
        // cover on every artwork refresh.
        private readonly HashSet<string> _failedArtworkUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _artworkLoadSync = new();
        private Task? _artworkLoadTask;
        private DateTime _nextArtworkRetryUtc;
        private bool _isTaskDetailsExpanded;

        public GameReleaseCandidate? ReleaseCandidate { get; set; }

        public GameCardViewModel(
            GameDefinition definition,
            GameInstallation installation,
            IGameManager gameManager,
            IGameLauncher gameLauncher,
            ISettingsService settingsService,
            IGameSearchAggregator searchAggregator,
            IGameArtworkService artworkService,
            NotificationService notifications,
            bool isLibraryRecoveryEntry = false)
        {
            _isLibraryRecoveryEntry = isLibraryRecoveryEntry;
            _definition = definition;
            _installation = installation;
            _gameManager = gameManager;
            _gameLauncher = gameLauncher;
            _settingsService = settingsService;
            _searchAggregator = searchAggregator;
            _artworkService = artworkService;
            _notifications = notifications;
            _coverUrl = definition.CoverUrl;
            _bannerUrl = definition.BannerUrl;
            RestorePersistedProgress();

            InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy && (State == InstallState.NotInstalled || State == InstallState.Error));
            PlayCommand = new AsyncRelayCommand(PlayAsync, () => !IsBusy && (State == InstallState.Installed || State == InstallState.UpdateAvailable));
            PauseCommand = new AsyncRelayCommand(PauseAsync, () => State == InstallState.Downloading || State == InstallState.Installing || State == InstallState.Verifying);
            ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => !IsBusy && (State == InstallState.Paused || (State == InstallState.Error && HasResumableSource)));
            RetryDownloadCommand = new AsyncRelayCommand(RetryDownloadAsync, () => !IsBusy && (State == InstallState.Error || State == InstallState.Paused));
            RemoveDownloadCommand = new AsyncRelayCommand(RemoveDownloadAsync, () => IsDownloadQueueItem && !IsCompletedDownload && !_isRemovingDownload && State != InstallState.Updating);
            DismissCompletedDownloadCommand = new RelayCommand(DismissCompletedDownload, () => IsCompletedDownload);
            OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
            MoveQueueUpCommand = new RelayCommand(() => _gameManager.MoveDownloadInQueue(Id, -1), () => IsDownloadQueueItem && !IsCompletedDownload);
            MoveQueueDownCommand = new RelayCommand(() => _gameManager.MoveDownloadInQueue(Id, 1), () => IsDownloadQueueItem && !IsCompletedDownload);
            ForceStartDownloadCommand = new RelayCommand(() => _gameManager.PromoteDownloadToFront(Id), () => IsQueued);
            ToggleTaskDetailsCommand = new RelayCommand(() => IsTaskDetailsExpanded = !IsTaskDetailsExpanded);
            VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => !IsBusy && (State == InstallState.Installed || State == InstallState.UpdateAvailable));
            UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !IsBusy && State == InstallState.UpdateAvailable);
            UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy && (State == InstallState.Installed || State == InstallState.UpdateAvailable || State == InstallState.Error));
            OpenFolderCommand = new RelayCommand(OpenFolder, () => State == InstallState.Installed || State == InstallState.UpdateAvailable);
            OpenDetailsCommand = new RelayCommand(OpenDetails);
            if (IsDownloadQueueItem)
                _ = EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);
        }

        public GameCardViewModel(
            GameReleaseCandidate candidate,
            GameInstallation installation,
            IGameManager gameManager,
            IGameLauncher gameLauncher,
            ISettingsService settingsService,
            IGameSearchAggregator searchAggregator,
            IGameArtworkService artworkService,
            NotificationService notifications)
        {
            ReleaseCandidate = candidate;
            _artworkService = artworkService;
            var safeId = !string.IsNullOrEmpty(candidate.Id) ? candidate.Id : Guid.NewGuid().ToString("N");
            // Some feeds occasionally return a release without a parsed title; keep a
            // readable name so the card never renders as an empty shell.
            var displayTitle = !string.IsNullOrWhiteSpace(candidate.CleanTitle)
                ? candidate.CleanTitle
                : !string.IsNullOrWhiteSpace(candidate.RawTitle)
                    ? candidate.RawTitle
                    : safeId;
            var description = string.IsNullOrWhiteSpace(candidate.RawTitle)
                && string.IsNullOrWhiteSpace(candidate.SourceName)
                    ? string.Empty
                    : candidate.RawTitle + LocalizationManager.GetString("CardSourceFormat", candidate.SourceName);
            _definition = new GameDefinition
            {
                Id = safeId,
                Name = displayTitle,
                Description = description,
                Developer = !string.IsNullOrEmpty(candidate.ReleaseAuthor) ? candidate.ReleaseAuthor : candidate.SourceName,
                Publisher = candidate.SourceName,
                Version = !string.IsNullOrEmpty(candidate.Version) ? candidate.Version : candidate.VersionType.ToString(),
                SizeBytes = candidate.VersionType == GameVersionType.Portable
                    ? candidate.DownloadSizeBytes
                    : candidate.EstimatedInstalledSizeBytes > 0
                        ? candidate.EstimatedInstalledSizeBytes
                        : candidate.DownloadSizeBytes,
                DownloadSizeBytes = candidate.DownloadSizeBytes,
                Categories = CreateReleaseCategories(candidate),
                SteamAppId = candidate.SteamAppId
            };

            if (!string.IsNullOrEmpty(candidate.TorrentFileUrl))
            {
                _definition.DownloadSources.Add(new DownloadSource { Type = "torrent", TorrentFileUrl = candidate.TorrentFileUrl, Priority = 1 });
            }
            if (!string.IsNullOrEmpty(candidate.MagnetLink))
            {
                _definition.DownloadSources.Add(new DownloadSource { Type = "torrent", MagnetLink = candidate.MagnetLink, Priority = 2 });
            }

            _installation = installation;
            _gameManager = gameManager;
            _gameLauncher = gameLauncher;
            _settingsService = settingsService;
            _searchAggregator = searchAggregator;
            _notifications = notifications;
            RestorePersistedProgress();

            InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy && (State == InstallState.NotInstalled || State == InstallState.Error));
            PlayCommand = new AsyncRelayCommand(PlayAsync, () => !IsBusy && (State == InstallState.Installed || State == InstallState.UpdateAvailable));
            PauseCommand = new AsyncRelayCommand(PauseAsync, () => State == InstallState.Downloading || State == InstallState.Installing || State == InstallState.Verifying);
            ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => !IsBusy && (State == InstallState.Paused || (State == InstallState.Error && HasResumableSource)));
            RetryDownloadCommand = new AsyncRelayCommand(RetryDownloadAsync, () => !IsBusy && (State == InstallState.Error || State == InstallState.Paused));
            RemoveDownloadCommand = new AsyncRelayCommand(RemoveDownloadAsync, () => IsDownloadQueueItem && !IsCompletedDownload && !_isRemovingDownload && State != InstallState.Updating);
            DismissCompletedDownloadCommand = new RelayCommand(DismissCompletedDownload, () => IsCompletedDownload);
            OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
            MoveQueueUpCommand = new RelayCommand(() => _gameManager.MoveDownloadInQueue(Id, -1), () => IsDownloadQueueItem && !IsCompletedDownload);
            MoveQueueDownCommand = new RelayCommand(() => _gameManager.MoveDownloadInQueue(Id, 1), () => IsDownloadQueueItem && !IsCompletedDownload);
            ForceStartDownloadCommand = new RelayCommand(() => _gameManager.PromoteDownloadToFront(Id), () => IsQueued);
            ToggleTaskDetailsCommand = new RelayCommand(() => IsTaskDetailsExpanded = !IsTaskDetailsExpanded);
            VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => !IsBusy && (State == InstallState.Installed || State == InstallState.UpdateAvailable));
            UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !IsBusy && State == InstallState.UpdateAvailable);
            UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy && (State == InstallState.Installed || State == InstallState.UpdateAvailable || State == InstallState.Error));
            OpenFolderCommand = new RelayCommand(OpenFolder, () => State == InstallState.Installed || State == InstallState.UpdateAvailable);
            OpenDetailsCommand = new RelayCommand(OpenDetails);
            if (IsDownloadQueueItem)
                _ = EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);
        }

        public AsyncRelayCommand InstallCommand { get; }
        public AsyncRelayCommand PlayCommand { get; }
        public AsyncRelayCommand PauseCommand { get; }
        public AsyncRelayCommand ResumeCommand { get; }
        public AsyncRelayCommand RetryDownloadCommand { get; }
        public AsyncRelayCommand RemoveDownloadCommand { get; }
        public RelayCommand DismissCompletedDownloadCommand { get; }
        public RelayCommand OpenDownloadFolderCommand { get; }
        public RelayCommand MoveQueueUpCommand { get; }
        public RelayCommand MoveQueueDownCommand { get; }
        public RelayCommand ForceStartDownloadCommand { get; }
        public RelayCommand ToggleTaskDetailsCommand { get; }
        public AsyncRelayCommand VerifyCommand { get; }
        public AsyncRelayCommand UpdateCommand { get; }
        public AsyncRelayCommand UninstallCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand OpenDetailsCommand { get; }
        public System.Windows.Input.ICommand PrimaryActionCommand => IsInstalled ? PlayCommand : InstallCommand;

        public bool IsLibraryRecoveryEntry => _isLibraryRecoveryEntry;
        public GameDefinition Definition => _definition;
        public GameInstallation Installation => _installation;
        public string Id => _definition.Id;
        public string Name => _definition.Name;
        public string Description => _definition.Description;
        public string Developer => _definition.Developer;
        public string Publisher => _definition.Publisher;
        public string Version => _definition.Version;
        public string VersionTypeDisplay => ReleaseCandidate?.VersionType switch
        {
            GameVersionType.Portable => "Portable",
            GameVersionType.Repack => "Repack",
            GameVersionType.Installer => LocalizationManager.GetString("InstallVersionInstaller"),
            GameVersionType.Archive => LocalizationManager.GetString("InstallVersionArchive"),
            _ => LocalizationManager.GetString("InstallVersionUnknown")
        };
        public string CoverUrl
        {
            get => _coverUrl;
            private set
            {
                if (SetProperty(ref _coverUrl, value))
                {
                    _definition.CoverUrl = value;
                    OnPropertyChanged(nameof(FeaturedImageUrl));
                    OnPropertyChanged(nameof(HasBannerArtwork));
                    OnPropertyChanged(nameof(HasCoverArtwork));
                }
            }
        }
        public string BannerUrl
        {
            get => _bannerUrl;
            private set
            {
                if (SetProperty(ref _bannerUrl, value))
                {
                    _definition.BannerUrl = value;
                    OnPropertyChanged(nameof(FeaturedImageUrl));
                    OnPropertyChanged(nameof(HasBannerArtwork));
                }
            }
        }
        public bool HasBannerArtwork => !string.IsNullOrWhiteSpace(BannerUrl)
            && (string.IsNullOrWhiteSpace(CoverUrl)
                || !string.Equals(BannerUrl.Trim(), CoverUrl.Trim(), StringComparison.OrdinalIgnoreCase));
        public bool HasCoverArtwork => !string.IsNullOrWhiteSpace(CoverUrl);
        public string FeaturedImageUrl => HasBannerArtwork ? BannerUrl : CoverUrl;

        public void ApplySteamArtwork(string coverUrl, string bannerUrl)
        {
            if (string.IsNullOrWhiteSpace(CoverUrl) && !string.IsNullOrWhiteSpace(coverUrl)
                && !IsArtworkRejected(coverUrl))
                CoverUrl = coverUrl;
            if (!HasBannerArtwork && !string.IsNullOrWhiteSpace(bannerUrl)
                && !IsArtworkRejected(bannerUrl))
                BannerUrl = bannerUrl;
        }

        private bool IsArtworkRejected(string url) => _failedArtworkUrls.Contains(url.Trim());

        public void UseCoverAsFeaturedFallback()
        {
            var failedUrl = FeaturedImageUrl;
            if (string.IsNullOrWhiteSpace(failedUrl))
                return;

            // Remember the broken URL so the artwork refresh cannot put it back
            // and make the banner blink between the broken image and the cover.
            _failedArtworkUrls.Add(failedUrl.Trim());

            if (HasBannerArtwork
                && string.Equals(BannerUrl.Trim(), failedUrl.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // The wide banner could not be decoded: drop it so the cover is used instead.
                BannerUrl = string.Empty;
                return;
            }

            if (string.Equals(CoverUrl?.Trim(), failedUrl.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // The cover itself is unusable: reload artwork from the local cache.
                CoverUrl = string.Empty;
                _ = EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);
            }
        }

        public void ApplyPreferredRelease(GameReleaseCandidate candidate)
        {
            if (candidate.VersionType != GameVersionType.Portable
                || ReleaseCandidate?.VersionType == GameVersionType.Portable)
                return;

            // We're switching from Repack → Portable: remember that both versions exist
            _hasAlternativeVersion = true;

            candidate.Id = Id;
            ReleaseCandidate = candidate;
            if (string.IsNullOrWhiteSpace(_definition.Description) || IsAutoGeneratedDescription(_definition.Description))
            {
                _definition.Description = candidate.RawTitle + LocalizationManager.GetString("CardSourceFormat", candidate.SourceName);
            }
            _definition.Version = !string.IsNullOrWhiteSpace(candidate.Version)
                ? candidate.Version
                : candidate.VersionType.ToString();
            _definition.SizeBytes = candidate.DownloadSizeBytes > 0
                ? candidate.DownloadSizeBytes
                : candidate.EstimatedInstalledSizeBytes;
            _definition.DownloadSizeBytes = candidate.DownloadSizeBytes;
            _definition.Categories = CreateReleaseCategories(candidate);
            _definition.DownloadSources.Clear();
            if (!string.IsNullOrEmpty(candidate.TorrentFileUrl))
                _definition.DownloadSources.Add(new DownloadSource
                    { Type = "torrent", TorrentFileUrl = candidate.TorrentFileUrl, Priority = 1 });
            if (!string.IsNullOrEmpty(candidate.MagnetLink))
                _definition.DownloadSources.Add(new DownloadSource
                    { Type = "torrent", MagnetLink = candidate.MagnetLink, Priority = 2 });

            OnPropertyChanged(nameof(ReleaseCandidate));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(VersionTypeDisplay));
            OnPropertyChanged(nameof(CategoriesDisplay));
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(DownloadSizeDisplay));
            OnPropertyChanged(nameof(TotalDownloadSizeDisplay));
            OnPropertyChanged(nameof(PopularityScore));
            OnPropertyChanged(nameof(ActiveDownloadsScore));
            OnPropertyChanged(nameof(ReleaseDateSortKey));
            OnPropertyChanged(nameof(DetailsText));

            if (!string.IsNullOrWhiteSpace(candidate.DetailsUrl))
            {
                _ = EnsureArtworkLoadedAsync(forceRefresh: !HasCoverArtwork, requireBanner: true, priority: ArtworkPriority.ModalDetails);
            }
        }

        private static bool IsAutoGeneratedDescription(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return true;

            return description.Contains("| Источник:")
                || description.Contains("| Source:")
                || description.Contains("| Джерело:");
        }

        private static System.Collections.Generic.List<string> CreateReleaseCategories(GameReleaseCandidate candidate)
        {
            var categories = new System.Collections.Generic.List<string>();
            if (candidate.PublishDate is DateTime published
                && published.Date >= DateTime.Today.AddDays(-6)
                && published.Date <= DateTime.Today)
            {
                categories.Add("Новинки");
            }

            categories.Add(candidate.SourceName);
            categories.Add(candidate.VersionType.ToString());
            return categories;
        }

        public string IconUrl => _definition.IconUrl;
        public string CategoriesDisplay
        {
            get
            {
                var parts = _definition.Categories.Select(category => category switch
                {
                    "Новинки" => LocalizationManager.GetString("CategoryNewReleases"),
                    "Каталог Vellum" or "Vellum Catalog" => LocalizationManager.GetString("BuiltInCatalogName"),
                    _ => category
                }).ToList();

                // If both Portable and Repack are available, replace the version type entry
                // with a combined badge so the user knows both options exist
                if (_hasAlternativeVersion && ReleaseCandidate?.VersionType == GameVersionType.Portable)
                {
                    var repackLabel = LocalizationManager.GetString("CategoryRepackAvailable", "Portable + Repack");
                    // Replace the "Portable" entry with "Portable + Repack"
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (parts[i] == "Portable")
                        {
                            parts[i] = repackLabel;
                            break;
                        }
                    }
                }

                return string.Join(", ", parts);
            }
        }
        public int PopularityScore => ReleaseCandidate?.Seeders ?? 0;
        public int ActiveDownloadsScore => ReleaseCandidate?.Leechers ?? 0;
        public DateTime ReleaseDateSortKey => ReleaseCandidate?.PublishDate ??
            (DateTime.TryParse(_definition.ReleaseDate, out var releaseDate) ? releaseDate : DateTime.MinValue);
        public string SizeDisplay => PathHelper.FormatBytes(_definition.SizeBytes);
        public string DownloadSizeDisplay => PathHelper.FormatBytes(_definition.DownloadSizeBytes);
        /// <summary>Installed size for the library summary; a measured size wins over the catalog estimate.</summary>
        public long LibrarySizeBytes => _installation is { InstalledSizeBytes: > 0 } installation
            ? installation.InstalledSizeBytes
            : _definition.SizeBytes;
        public string TotalDownloadSizeDisplay => PathHelper.FormatBytes(EffectiveTotalBytes);
        public string DownloadedBytesDisplay => PathHelper.FormatBytes(EffectiveDownloadedBytes);
        public long CurrentDownloadSpeedBps => (State == InstallState.Downloading && !IsQueued && _progress != null)
            ? Math.Max(0, _progress.SpeedBytesPerSec)
            : 0;
        public long CurrentUploadSpeedBps => (State == InstallState.Downloading && !IsQueued && _progress != null)
            ? Math.Max(0, _progress.UploadSpeedBytesPerSec)
            : 0;
        public string DownloadSpeedDisplay => CurrentDownloadSpeedBps > 0
            ? LocalizationManager.GetString("SpeedUnitFormat", PathHelper.FormatBytes(CurrentDownloadSpeedBps))
            : "—";
        public string UploadSpeedText => CurrentUploadSpeedBps > 0
            ? LocalizationManager.GetString("SpeedUnitFormat", PathHelper.FormatBytes(CurrentUploadSpeedBps))
            : "—";
        public string PeersDisplay
        {
            get
            {
                if (_progress != null && (_progress.Peers > 0 || _progress.TotalPeers > 0))
                {
                    var total = _progress.TotalPeers > _progress.Peers
                        ? _progress.TotalPeers
                        : (ReleaseCandidate?.Seeders > _progress.Peers ? ReleaseCandidate.Seeders : _progress.Peers);
                    return total > 0
                        ? $"{_progress.Peers} ({total})"
                        : _progress.Peers.ToString();
                }

                if (ReleaseCandidate?.Seeders > 0 && (IsDownloading || IsPaused || IsError))
                    return $"0 ({ReleaseCandidate.Seeders})";

                return "—";
            }
        }

        public string RemainingTimeDisplay => State == InstallState.Downloading && !IsQueued && _progress != null && _progress.SecondsRemaining > 0
            ? PathHelper.FormatTimeRemaining(_progress.SecondsRemaining)
            : "—";

        public bool IsTaskDetailsExpanded
        {
            get => _isTaskDetailsExpanded;
            set => SetProperty(ref _isTaskDetailsExpanded, value);
        }

        public bool IsQueued => State == InstallState.Downloading && (_installation?.IsQueued == true);
        public int QueueOrder => _installation?.QueueOrder ?? 0;
        public int DownloadSortPriority
        {
            get
            {
                if (IsCompletedDownload) return 4;
                if (IsPaused) return 3;
                if (IsError) return 2;
                if (IsQueued) return 1;
                if (IsDownloading) return 0;
                return 5;
            }
        }
        public bool IsError => State == InstallState.Error;
        public bool IsCompletedDownload => State == InstallState.Installed && (_installation?.CompletedInSession == true);
        public bool HasDownloadError => State == InstallState.Error || !string.IsNullOrWhiteSpace(_installation?.LastErrorMessage);
        public string DownloadErrorText => !string.IsNullOrWhiteSpace(_installation?.LastErrorMessage)
            ? _installation!.LastErrorMessage!
            : LocalizationManager.GetString("DownloadSubError", "Произошла ошибка во время загрузки или установки.");
        public bool IsDownloadStalled => State == InstallState.Downloading && !IsQueued && (_progress?.IsStalled == true);
        public string SourceProtocolDisplay => (_progress?.IsTorrent ?? HasTorrentSource) ? "BitTorrent" : "HTTP / Direct";
        public string CurrentFileText => !string.IsNullOrWhiteSpace(_progress?.CurrentFile) && !string.Equals(_progress.CurrentFile, Id, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(_progress.CurrentFile)
            : SourceProtocolDisplay;

        private string? CurrentPhaseKey
            => _progress?.PhaseKey ?? DownloadPhases.FromLegacyText(_progress?.Phase);

        /// <summary>
        /// Canonical visual state key for the Downloads page:
        /// Queued | Preparing | Waiting | Downloading | Paused | Verifying | Installing | Finalizing | Completed | Error
        /// Uses <see cref="DownloadPhases"/> keys, so translating the UI never changes
        /// which state a download is shown in.
        /// </summary>
        public string DownloadVisualState
        {
            get
            {
                if (IsCompletedDownload)
                    return "Completed";
                if (State == InstallState.Error)
                    return "Error";
                if (State == InstallState.Paused)
                    return "Paused";
                if (IsQueued)
                    return "Queued";

                var phaseKey = CurrentPhaseKey;
                var fraction = _progress?.Fraction ?? 0;
                var hasTotal = _progress?.TotalBytes > 0;
                var legacyPhase = _progress?.Phase ?? string.Empty;

                if (State == InstallState.Verifying
                    || phaseKey is DownloadPhases.CheckingIntegrity or DownloadPhases.Verifying
                    || (phaseKey == null && IsPhaseCheckingIntegrity(legacyPhase)))
                {
                    return "Verifying";
                }

                if (State == InstallState.Installing || State == InstallState.Updating)
                {
                    if (phaseKey is DownloadPhases.MovingFiles or DownloadPhases.Finalizing or DownloadPhases.Complete
                        || (hasTotal && fraction >= 0.99 && phaseKey == DownloadPhases.Installing)
                        || (phaseKey == null && fraction >= 0.99
                            && string.Equals(legacyPhase, "Installing", StringComparison.OrdinalIgnoreCase)))
                    {
                        return "Finalizing";
                    }

                    return "Installing";
                }

                if (State == InstallState.Downloading)
                {
                    if (phaseKey == DownloadPhases.Complete || (hasTotal && fraction >= 1.0))
                        return "Finalizing";
                    if (phaseKey is DownloadPhases.Starting or DownloadPhases.FetchingMetadata
                        or DownloadPhases.CheckingDiskSpace or DownloadPhases.Queued)
                    {
                        return "Preparing";
                    }
                    if (phaseKey == DownloadPhases.SearchingPeers)
                        return "Waiting";

                    // Fallback for sources that only provide a display string.
                    if (phaseKey == null)
                    {
                        if (IsPhaseWaitingPeers(legacyPhase))
                            return "Waiting";
                        if (IsPhasePreparing(legacyPhase))
                            return "Preparing";
                    }

                    return "Downloading";
                }

                return State == InstallState.Installed ? "Completed" : "Paused";
            }
        }

        private bool IsPhaseCheckingIntegrity(string phase)
            => string.Equals(phase, "Verifying", StringComparison.OrdinalIgnoreCase)
               || string.Equals(phase, LocalizationManager.GetString("TorrentPhaseCheckingIntegrity"), StringComparison.OrdinalIgnoreCase)
               || phase.Contains("целостност", StringComparison.OrdinalIgnoreCase)
               || phase.Contains("цілісност", StringComparison.OrdinalIgnoreCase)
               || phase.Contains("integrity", StringComparison.OrdinalIgnoreCase);

        private bool IsPhasePreparing(string phase)
        {
            if (_progress == null)
                return true;
            if (string.Equals(phase, "Checking available disk space", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phase, LocalizationManager.GetString("DownloadPhaseCheckingDiskSpace"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(phase, LocalizationManager.GetString("TorrentPhaseStarting"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(phase, LocalizationManager.GetString("TorrentPhaseFetchingMetadata"), StringComparison.OrdinalIgnoreCase)
                || phase.Contains("метадан", StringComparison.OrdinalIgnoreCase)
                || phase.Contains("metadata", StringComparison.OrdinalIgnoreCase)
                || phase.Contains("торрент-клиент", StringComparison.OrdinalIgnoreCase)
                || phase.Contains("торент-клієнт", StringComparison.OrdinalIgnoreCase)
                || phase.Contains("torrent client", StringComparison.OrdinalIgnoreCase)
                || phase.Contains("места на диске", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(phase, "Downloading", StringComparison.OrdinalIgnoreCase)
                   && _progress.BytesDownloaded <= 0
                   && _progress.TotalBytes <= 0
                   && _progress.SpeedBytesPerSec <= 0;
        }

        private bool IsPhaseWaitingPeers(string phase)
        {
            if (string.Equals(phase, LocalizationManager.GetString("TorrentPhaseSearchingPeers"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(phase, LocalizationManager.GetString("TorrentPhaseSearchingPeersShort"), StringComparison.OrdinalIgnoreCase)
                || phase.Contains("Поиск пиров", StringComparison.OrdinalIgnoreCase)
                || phase.Contains("Пошук пірів", StringComparison.OrdinalIgnoreCase)
                || phase.Contains("Searching for peers", StringComparison.OrdinalIgnoreCase))
            {
                return _progress == null || _progress.SpeedBytesPerSec <= 0;
            }
            return false;
        }

        public string DownloadStatusText => DownloadVisualState switch
        {
            "Queued" => LocalizationManager.GetString("DownloadStateQueued", "В очереди"),
            "Preparing" => LocalizationManager.GetString("DownloadStatePreparing", "Подготовка"),
            "Waiting" => LocalizationManager.GetString("DownloadStateWaiting", "Ожидание пиров"),
            "Downloading" => LocalizationManager.GetString("StatusDownloading", "Загружается"),
            "Paused" => LocalizationManager.GetString("StatusPaused", "Приостановлена"),
            "Verifying" => LocalizationManager.GetString("DownloadStateVerifying", "Проверка файлов"),
            "Installing" => LocalizationManager.GetString("StatusInstalling", "Установка"),
            "Finalizing" => LocalizationManager.GetString("DownloadStateFinalizing", "Завершение"),
            "Completed" => LocalizationManager.GetString("DownloadStateCompleted", "Завершена"),
            "Error" => LocalizationManager.GetString("StatusError", "Ошибка"),
            _ => StatusText
        };

        public string DownloadStageSubtitle
        {
            get
            {
                var vs = DownloadVisualState;
                return vs switch
                {
                    "Queued" => LocalizationManager.GetString("DownloadSubQueued", "Ожидание свободного слота в очереди…"),
                    "Preparing" => LocalizeRawPhase(_progress?.Phase, LocalizationManager.GetString("DownloadSubPreparing", "Подготовка файлов и проверка хранилища…")),
                    "Waiting" => LocalizeRawPhase(_progress?.Phase, LocalizationManager.GetString("TorrentPhaseSearchingPeers", "Поиск пиров через DHT и трекеры…")),
                    "Downloading" => CurrentPhaseKey == DownloadPhases.CopyingFiles
                        ? LocalizationManager.GetString("DownloadPhaseCopyingFiles", "Копирование файлов…")
                        : $"{SourceProtocolDisplay} • {VersionTypeDisplay}",
                    "Verifying" => !string.IsNullOrWhiteSpace(_progress?.CurrentFile) && !string.Equals(_progress.CurrentFile, Id, StringComparison.OrdinalIgnoreCase)
                        ? $"{LocalizationManager.GetString("TorrentPhaseCheckingIntegrity", "Проверка целостности данных…")} ({Path.GetFileName(_progress.CurrentFile)})"
                        : LocalizationManager.GetString("TorrentPhaseCheckingIntegrity", "Проверка целостности данных…"),
                    "Installing" => LocalizeRawPhase(_progress?.Phase, LocalizationManager.GetString("DownloadSubInstalling", "Распаковка и установка файлов игры…")),
                    "Finalizing" => LocalizationManager.GetString("DownloadSubFinalizing", "Завершение установки и очистка временных файлов…"),
                    "Paused" => LocalizationManager.GetString("DownloadSubPaused", "Загрузка приостановлена — прогресс сохранён"),
                    "Completed" => LocalizationManager.GetString("DownloadSubCompleted", "Игра успешно установлена и готова к запуску"),
                    "Error" => !string.IsNullOrWhiteSpace(Publisher)
                        ? $"{SourceProtocolDisplay} • {Publisher}"
                        : SourceProtocolDisplay,
                    _ => Description
                };
            }
        }

        private string LocalizeRawPhase(string? rawPhase, string fallback)
        {
            if (string.IsNullOrWhiteSpace(rawPhase))
                return fallback;

            // Never display a stale "Paused" phase string when the card is not paused.
            if (State != InstallState.Paused && (
                string.Equals(rawPhase, "Пауза", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawPhase, "Paused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawPhase, LocalizationManager.GetString("StatusPaused"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawPhase, LocalizationManager.GetString("TorrentPhasePaused"), StringComparison.OrdinalIgnoreCase)))
            {
                return fallback;
            }

            return rawPhase switch
            {
                "Downloading" => fallback,
                "Checking available disk space" => LocalizationManager.GetString("DownloadPhaseCheckingDiskSpace", "Проверка места на диске…"),
                "Copying files" => LocalizationManager.GetString("DownloadPhaseCopyingFiles", "Копирование файлов…"),
                "Verifying" => LocalizationManager.GetString("TorrentPhaseCheckingIntegrity", "Проверка целостности данных…"),
                "Installing" => LocalizationManager.GetString("DownloadSubInstalling", "Распаковка и установка файлов игры…"),
                "Extracting" => !string.IsNullOrWhiteSpace(_progress?.CurrentFile)
                    ? $"{LocalizationManager.GetString("DownloadPhaseExtracting", "Распаковка архива…")} ({Path.GetFileName(_progress.CurrentFile)})"
                    : LocalizationManager.GetString("DownloadPhaseExtracting", "Распаковка архива…"),
                "Mounting image" => LocalizationManager.GetString("DownloadPhaseMountingIso", "Монтирование ISO-образа…"),
                "Running installer" => LocalizationManager.GetString("DownloadPhaseRunningInstaller", "Выполнение установщика…"),
                "Waiting for installer" or "Waiting for installer (as Administrator)" => LocalizationManager.GetString("DownloadPhaseWaitingInstaller", "Ожидание завершения установщика…"),
                "Installing prerequisites" => LocalizationManager.GetString("DownloadPhasePrerequisites", "Установка системных компонентов (VCRedist)…"),
                "Moving files" => LocalizationManager.GetString("DownloadPhaseMovingFiles", "Перемещение файлов в библиотеку…"),
                "Extracting installer tools" or "Extracting game files directly (installer crashed)" => LocalizationManager.GetString("DownloadPhaseDirectExtract", "Прямая распаковка игровых архивов…"),
                "Extraction complete" or "Complete" => LocalizationManager.GetString("DownloadSubFinalizing", "Завершение установки и очистка временных файлов…"),
                _ => rawPhase
            };
        }
        public string DownloadPathDisplay
        {
            get
            {
                var libraryPath = Path.GetDirectoryName(InstallPath);
                if (string.IsNullOrWhiteSpace(libraryPath))
                    libraryPath = _settingsService.Settings.DefaultInstallPath;
                return ReleaseCandidate?.VersionType == GameVersionType.Portable
                    ? InstallPath
                    : PathHelper.GetStagingPath(libraryPath, Id);
            }
        }
        public string DetailsText => string.Join(Environment.NewLine, new[]
        {
            $"{LocalizationManager.GetString("DetailName")}: {Name}",
            $"{LocalizationManager.GetString("DetailDescription")}: {Description}",
            $"{LocalizationManager.GetString("DetailCategories")}: {CategoriesDisplay}",
            $"{LocalizationManager.GetString("DetailDeveloper")}: {Developer}",
            $"{LocalizationManager.GetString("DetailPublisher")}: {Publisher}",
            $"{LocalizationManager.GetString("DetailVersion")}: {Version}",
            $"{LocalizationManager.GetString("DetailInstallSize")}: {SizeDisplay}",
            $"{LocalizationManager.GetString("DetailDownloadSize")}: {TotalDownloadSizeDisplay}",
            $"{LocalizationManager.GetString("DetailStatus")}: {StatusText}",
            ReleaseCandidate is null ? null : $"{LocalizationManager.GetString("DetailOriginalName")}: {ReleaseCandidate.RawTitle}",
            ReleaseCandidate is null ? null : LocalizationManager.GetString("DetailSeedersFormat", ReleaseCandidate.Seeders)
        }.Where(line => !string.IsNullOrWhiteSpace(line)));

        public Task EnsureArtworkLoadedAsync(bool forceRefresh = false, bool requireBanner = false, ArtworkPriority priority = ArtworkPriority.Background)
        {
            if (priority == ArtworkPriority.ModalDetails)
            {
                forceRefresh = true;
            }

            if (!forceRefresh && !string.IsNullOrWhiteSpace(CoverUrl) && (!requireBanner || HasBannerArtwork))
                return Task.CompletedTask;

            lock (_artworkLoadSync)
            {
                if (_artworkLoadTask != null && !forceRefresh)
                    return _artworkLoadTask;
                if (!forceRefresh && DateTime.UtcNow < _nextArtworkRetryUtc)
                    return Task.CompletedTask;

                var task = Task.Run(() => LoadArtworkAsync(forceRefresh, requireBanner, priority));
                _artworkLoadTask = task;
                _ = task.ContinueWith(completed =>
                {
                    lock (_artworkLoadSync)
                    {
                        if (!ReferenceEquals(_artworkLoadTask, completed))
                            return;

                        _artworkLoadTask = null;
                        if (!HasCoverArtwork || (requireBanner && !HasBannerArtwork))
                        {
                            var cooldownSeconds = priority switch
                            {
                                ArtworkPriority.ModalDetails => 1,
                                ArtworkPriority.Visible => 3,
                                _ => 25
                            };
                            _nextArtworkRetryUtc = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                        }
                        else
                        {
                            _nextArtworkRetryUtc = DateTime.MinValue;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
        }

        private async Task LoadArtworkAsync(bool replaceCachedArtwork, bool requireBanner, ArtworkPriority priority)
        {
            if (!replaceCachedArtwork && !string.IsNullOrWhiteSpace(CoverUrl) && (!requireBanner || HasBannerArtwork))
                return;

            try
            {
                // Artwork services are English-language. Prefer the canonical release title,
                // then the localized alternative and finally the raw release title.
                var artworkTitles = new[]
                    {
                        ReleaseCandidate?.CleanTitle,
                        Name,
                        ReleaseCandidate?.AltTitle,
                        ReleaseCandidate?.RawTitle
                    }
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .Select(title => title!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();

                foreach (var artworkTitle in artworkTitles)
                {
                    var artwork = await _artworkService.FindArtworkAsync(artworkTitle, ReleaseCandidate?.DetailsUrl, priority, replaceCachedArtwork)
                        .ConfigureAwait(false);
                    if (artwork == null)
                        continue;

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null)
                        return;

                    await dispatcher.InvokeAsync(() =>
                    {
                        if ((replaceCachedArtwork || string.IsNullOrWhiteSpace(CoverUrl))
                            && !string.IsNullOrWhiteSpace(artwork.CoverPath))
                            CoverUrl = artwork.CoverPath;
                        if ((replaceCachedArtwork || !HasBannerArtwork)
                            && !string.IsNullOrWhiteSpace(artwork.BannerPath))
                            BannerUrl = artwork.BannerPath;
                    });
                    return;
                }
            }
            catch
            {
                // Keep the existing placeholder if the artwork service is offline or unavailable.
            }
        }

        public bool IsBusy
        {
            get => _isActionBusy || _isRemovingDownload;
            private set
            {
                if (!SetProperty(ref _isActionBusy, value))
                    return;

                OnPropertyChanged(nameof(IsBusy));
                RaiseAllCommandsCanExecuteChanged();
            }
        }

        private void RaiseAllCommandsCanExecuteChanged()
        {
            InstallCommand.RaiseCanExecuteChanged();
            PlayCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            RetryDownloadCommand.RaiseCanExecuteChanged();
            RemoveDownloadCommand.RaiseCanExecuteChanged();
            DismissCompletedDownloadCommand.RaiseCanExecuteChanged();
            MoveQueueUpCommand.RaiseCanExecuteChanged();
            MoveQueueDownCommand.RaiseCanExecuteChanged();
            ForceStartDownloadCommand.RaiseCanExecuteChanged();
            VerifyCommand.RaiseCanExecuteChanged();
            UpdateCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }

        private void SetRemovingDownload(bool value)
        {
            if (_isRemovingDownload == value)
                return;

            _isRemovingDownload = value;
            OnPropertyChanged(nameof(IsBusy));
            RaiseAllCommandsCanExecuteChanged();
        }

        public InstallState State
        {
            get => _installation?.State ?? InstallState.NotInstalled;
            private set
            {
                if (_installation == null)
                    _installation = new GameInstallation { GameId = _definition.Id };

                if (_installation.State != value)
                {
                    _installation.State = value;
                    NotifyStateChanges();
                }
            }
        }

        public string InstallPath => _installation?.InstallPath ?? string.Empty;
        public string InstalledVersion => _installation?.InstalledVersion ?? string.Empty;

        public bool IsInstalled => State == InstallState.Installed || State == InstallState.UpdateAvailable;
        public bool IsDownloading => State == InstallState.Downloading || State == InstallState.Installing || State == InstallState.Verifying || State == InstallState.Updating;
        public bool IsPaused => State == InstallState.Paused;
        public bool HasResumableSource => _installation?.PendingRelease != null
            || ReleaseCandidate != null
            || _definition.DownloadSources.Count > 0
            || (_installation?.DownloadedBytes ?? 0) > 0;
        public bool IsDownloadQueueItem => State is InstallState.Downloading or InstallState.Paused
            or InstallState.Verifying or InstallState.Installing or InstallState.Updating
            || (State == InstallState.Error && (HasResumableSource || !string.IsNullOrWhiteSpace(_installation?.LastErrorMessage)))
            || IsCompletedDownload;
        public bool IsActiveTorrentDownload => (State is InstallState.Downloading or InstallState.Verifying or InstallState.Installing)
            && !IsQueued
            && IsTorrentDownload;
        public bool IsTorrentDownload => Progress?.IsTorrent ?? HasTorrentSource;
        public long DownloadTotalBytes => EffectiveTotalBytes;
        public long DownloadedBytes => EffectiveDownloadedBytes;
        private bool HasTorrentSource => _definition.DownloadSources.Any(source =>
            string.Equals(source.Type, "torrent", StringComparison.OrdinalIgnoreCase));
        public bool IsRunning => _gameLauncher.IsRunning(Id);
        public string PlayButtonText => IsRunning
            ? LocalizationManager.GetString("StatusRunning")
            : LocalizationManager.GetString("CardPlay");

        public string StatusText => State switch
        {
            InstallState.NotInstalled => LocalizationManager.GetString("StatusNotInstalled"),
            InstallState.Downloading => LocalizationManager.GetString("StatusDownloading"),
            InstallState.Paused => LocalizationManager.GetString("StatusPaused"),
            InstallState.Verifying => LocalizationManager.GetString("StatusVerifying"),
            InstallState.Installing => LocalizationManager.GetString("StatusInstalling"),
            InstallState.Installed => LocalizationManager.GetString(IsRunning ? "StatusRunning" : "StatusInstalled"),
            InstallState.UpdateAvailable => LocalizationManager.GetString("StatusUpdateAvailable"),
            InstallState.Updating => LocalizationManager.GetString("StatusUpdating"),
            InstallState.Uninstalling => LocalizationManager.GetString("StatusUninstalling"),
            InstallState.Error => LocalizationManager.GetString("StatusError"),
            _ => LocalizationManager.GetString("StatusUnknown")
        };

        private void RestorePersistedProgress()
        {
            var restored = BuildPersistedProgress();
            if (restored != null)
                _progress = restored;
        }

        private DownloadProgress? BuildPersistedProgress()
        {
            if (_installation == null)
                return null;
            if (_installation.State is not (InstallState.Paused or InstallState.Error) && !_installation.IsQueued)
                return null;

            var total = _installation.DownloadTotalBytes > 0
                ? _installation.DownloadTotalBytes
                : (_definition.DownloadSizeBytes > 0 ? _definition.DownloadSizeBytes : _definition.SizeBytes);
            if (total <= 0 && _installation.DownloadedBytes <= 0)
                return null;

            var downloaded = Math.Max(0, _installation.DownloadedBytes);
            return new DownloadProgress
            {
                GameId = _installation.GameId,
                Fraction = total > 0 ? Math.Clamp((double)downloaded / total, 0.0, 1.0) : 0.0,
                BytesDownloaded = downloaded,
                TotalBytes = total,
                IsTorrent = _installation.DownloadWasTorrent || HasTorrentSource,
                Phase = _installation.State == InstallState.Error
                    ? LocalizationManager.GetString("StatusError")
                    : LocalizationManager.GetString("StatusPaused")
            };
        }

        private long EffectiveTotalBytes
        {
            get
            {
                if (_progress != null && _progress.TotalBytes > 0)
                    return _progress.TotalBytes;
                if (_installation != null && _installation.DownloadTotalBytes > 0)
                    return _installation.DownloadTotalBytes;
                if (_definition.DownloadSizeBytes > 0)
                    return _definition.DownloadSizeBytes;
                return _definition.SizeBytes;
            }
        }

        private long EffectiveDownloadedBytes
        {
            get
            {
                if (IsCompletedDownload)
                    return EffectiveTotalBytes;
                if (_progress != null && _progress.BytesDownloaded > 0)
                    return _progress.BytesDownloaded;
                if (_installation != null && _installation.DownloadedBytes > 0)
                    return _installation.DownloadedBytes;
                return 0;
            }
        }

        /// <summary>Applies a progress snapshot coming from the restart auto-resume path.</summary>
        public void ReportProgress(DownloadProgress progress)
        {
            if (progress != null && !string.IsNullOrEmpty(progress.GameId)
                && !string.Equals(progress.GameId, Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Progress = progress;
        }

        public DownloadProgress? Progress
        {
            get => _progress;
            private set
            {
                _progress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(IsActiveTorrentDownload));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(SpeedText));
                OnPropertyChanged(nameof(DownloadSpeedDisplay));
                OnPropertyChanged(nameof(CurrentDownloadSpeedBps));
                OnPropertyChanged(nameof(CurrentUploadSpeedBps));
                OnPropertyChanged(nameof(TimeRemainingText));
                OnPropertyChanged(nameof(PhaseText));
                OnPropertyChanged(nameof(UploadSpeedText));
                OnPropertyChanged(nameof(PeersDisplay));
                OnPropertyChanged(nameof(DownloadedBytesDisplay));
                OnPropertyChanged(nameof(TotalDownloadSizeDisplay));
                OnPropertyChanged(nameof(RemainingTimeDisplay));
                OnPropertyChanged(nameof(DownloadVisualState));
                OnPropertyChanged(nameof(DownloadStatusText));
                OnPropertyChanged(nameof(DownloadStageSubtitle));
                OnPropertyChanged(nameof(IsDownloadStalled));
                OnPropertyChanged(nameof(CurrentFileText));
            }
        }

        public double ProgressPercent
        {
            get
            {
                if (IsCompletedDownload)
                    return 100.0;
                if (_progress != null && _progress.Fraction > 0)
                    return _progress.Percent;
                var total = EffectiveTotalBytes;
                var downloaded = EffectiveDownloadedBytes;
                if (total > 0 && downloaded > 0)
                    return Math.Round(Math.Clamp((double)downloaded / total, 0.0, 1.0) * 100, 1);
                return 0;
            }
            set { }
        }
        public string PhaseText => LocalizeRawPhase(_progress?.Phase, DownloadStatusText);
        public string ProgressText
        {
            get
            {
                var total = EffectiveTotalBytes;
                var downloaded = EffectiveDownloadedBytes;
                if (total > 0)
                    return $"{PathHelper.FormatBytes(downloaded)} / {PathHelper.FormatBytes(total)}";
                if (downloaded > 0)
                    return PathHelper.FormatBytes(downloaded);
                return "—";
            }
        }

        public string SpeedText => CurrentDownloadSpeedBps > 0
            ? LocalizationManager.GetString("SpeedUnitFormat", PathHelper.FormatBytes(CurrentDownloadSpeedBps))
            : string.Empty;

        public string TimeRemainingText => State == InstallState.Downloading && !IsQueued && _progress != null && _progress.SecondsRemaining > 0
            ? LocalizationManager.GetString("TimeRemainingFormat", PathHelper.FormatTimeRemaining(_progress.SecondsRemaining))
            : string.Empty;

        public void NotifyLocalizationChanged()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(DownloadVisualState));
            OnPropertyChanged(nameof(DownloadStatusText));
            OnPropertyChanged(nameof(DownloadStageSubtitle));
            OnPropertyChanged(nameof(DownloadErrorText));
            OnPropertyChanged(nameof(PhaseText));
            OnPropertyChanged(nameof(TimeRemainingText));
            OnPropertyChanged(nameof(RemainingTimeDisplay));
            OnPropertyChanged(nameof(DetailsText));
            OnPropertyChanged(nameof(VersionTypeDisplay));
            OnPropertyChanged(nameof(CategoriesDisplay));
        }

        public void NotifyRunningChanged()
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PlayButtonText));
        }

        public void UpdateInstallation(GameInstallation? installation)
        {
            // A missing installation record must still leave the card usable: a null
            // here used to make every state binding throw and render an empty card.
            _installation = installation ?? new GameInstallation { GameId = _definition.Id };
            if ((_installation.State is InstallState.Paused or InstallState.Error) && _progress == null)
                RestorePersistedProgress();
            NotifyStateChanges();
        }

        private void NotifyStateChanges()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(DownloadVisualState));
            OnPropertyChanged(nameof(DownloadStatusText));
            OnPropertyChanged(nameof(DownloadStageSubtitle));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsQueued));
            OnPropertyChanged(nameof(QueueOrder));
            OnPropertyChanged(nameof(DownloadSortPriority));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsCompletedDownload));
            OnPropertyChanged(nameof(HasDownloadError));
            OnPropertyChanged(nameof(DownloadErrorText));
            OnPropertyChanged(nameof(IsDownloadStalled));
            OnPropertyChanged(nameof(IsDownloadQueueItem));
            OnPropertyChanged(nameof(IsActiveTorrentDownload));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(InstallPath));
            OnPropertyChanged(nameof(DownloadPathDisplay));
            OnPropertyChanged(nameof(InstalledVersion));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(DownloadSpeedDisplay));
            OnPropertyChanged(nameof(UploadSpeedText));
            OnPropertyChanged(nameof(RemainingTimeDisplay));
            OnPropertyChanged(nameof(PeersDisplay));

            if (IsDownloadQueueItem && (!HasCoverArtwork || !HasBannerArtwork))
                _ = EnsureArtworkLoadedAsync(requireBanner: true, priority: ArtworkPriority.Visible);

            RaiseAllCommandsCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
        }

        private void OpenDetails()
        {
            // Opening details explicitly retries a missing cover or banner with top priority.
            _ = EnsureArtworkLoadedAsync(forceRefresh: true, requireBanner: true, priority: ArtworkPriority.ModalDetails);
            var steamMetadata = global::Vellum.App.App.Services?.GetService(typeof(ISteamMetadataService))
                as ISteamMetadataService;
            var window = new GameDetailsWindow(this, _searchAggregator, _settingsService.Settings.DefaultInstallPath, steamMetadata)
            {
                Owner = Application.Current?.MainWindow
            };
            window.ShowDialog();
        }

        private async Task InstallAsync()
        {
            var defaultBase = _settingsService.Settings.DefaultInstallPath;
            if (string.IsNullOrWhiteSpace(defaultBase))
                defaultBase = PathHelper.DefaultLibraryPath;

            string chosenBase;

            if (_settingsService.Settings.AskInstallFolderOnEachInstall)
            {
                // Пользователь включил «спрашивать папку» — показываем диалог
                var folderDialog = new OpenFolderDialog
                {
                    Title = LocalizationManager.GetString("BrowseGameInstallFolderFormat", Name),
                    InitialDirectory = Directory.Exists(defaultBase) ? defaultBase : @"C:\"
                };

                if (folderDialog.ShowDialog() != true) return;

                chosenBase = PathHelper.GetLibraryRoot(folderDialog.FolderName);

                // Запоминаем выбор как новый путь по умолчанию
                _settingsService.Settings.DefaultInstallPath = chosenBase;
                await _settingsService.SaveAsync();
            }
            else
            {
                // Тихая установка — сразу в библиотеку из настроек
                chosenBase = defaultBase;
                PathHelper.EnsureDirectory(chosenBase);
            }

            // If the install was started from the "Информация об игре" window, keep it as the owner
            // (so the dialog is not hidden behind it) and close it once the download is confirmed.
            var detailsWindow = Application.Current.Windows
                .OfType<GameDetailsWindow>()
                .FirstOrDefault(w => w.IsVisible);

            // Show the search + disk-space dialog
            var dialog = new InstallDialogWindow(_searchAggregator, Name, chosenBase, ReleaseCandidate)
            {
                Owner = (Window?)detailsWindow ?? Application.Current.MainWindow
            };

            if (dialog.ShowDialog() != true || !dialog.DownloadConfirmed) return;

            detailsWindow?.Close();

            var release = dialog.SelectedRelease;
            if (release != null)
            {
                if (string.IsNullOrWhiteSpace(release.MagnetLink)
                    && string.IsNullOrWhiteSpace(release.TorrentFileUrl)
                    && string.IsNullOrWhiteSpace(release.DirectUrl))
                {
                    MessageBox.Show(LocalizationManager.GetString("InstallMissingDownloadSource"),
                        LocalizationManager.GetString("InstallMissingDownloadSourceTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // The dialog usually returns a DIFFERENT candidate object than this card holds
                // (every parse creates a new Guid). GameManager tracks the installation by release.Id,
                // so without this line the state, progress and the Downloads tab never reach this card.
                release.Id = Id;
                ReleaseCandidate = release;
                OnPropertyChanged(nameof(ReleaseCandidate));
                OnPropertyChanged(nameof(VersionTypeDisplay));
                OnPropertyChanged(nameof(DownloadPathDisplay));
                OnPropertyChanged(nameof(DetailsText));
            }

            IsBusy = true;
            _cts = new CancellationTokenSource();

            var progressReporter = new Progress<DownloadProgress>(p =>
            {
                Application.Current?.Dispatcher?.Invoke(() => Progress = p);
            });

            var offerToPlay = false;
            try
            {
                OperationResult result;

                if (release != null)
                {
                    // Use release candidate from aggregator (external source): magnet -> torrent download
                    result = await _gameManager.InstallReleaseAsync(
                        release, chosenBase, progressReporter, _cts.Token);
                }
                else
                {
                    // Fallback: install from built-in catalog definition
                    result = await _gameManager.InstallAsync(Id, chosenBase, progressReporter, _cts.Token);
                }

                UpdateInstallation(_gameManager.GetInstallation(Id));

                if (_isRemovingDownload || State == InstallState.NotInstalled)
                {
                    Progress = null;
                    return;
                }

                // A pause (user-initiated, or on shutdown) is not an install failure:
                // the GameManager leaves the state Paused so the Continue button stays
                // usable. Restore a frozen snapshot instead of clearing the progress.
                var isPaused = State == InstallState.Paused;
                Progress = (isPaused || State == InstallState.Error) ? BuildPersistedProgress() ?? Progress : null;
                offerToPlay = result.Success && release?.VersionType == GameVersionType.Portable;

                if (result.Success)
                {
                    _notifications.Show(
                        LocalizationManager.GetString("InstallCompleteTitle"),
                        LocalizationManager.GetString("ToastInstallDoneFormat", Name));
                }
                else if (isPaused || (_cts?.IsCancellationRequested ?? false))
                {
                    // Deliberate pause: the card already shows "Paused" with Continue.
                }
                else
                {
                    MessageBox.Show(LocalizationManager.GetString("InstallFailedFormat",
                            result.ErrorMessage ?? LocalizationManager.GetString("InstallUnknown")),
                        LocalizationManager.GetString("InstallErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsBusy = false;
            }

            if (offerToPlay && MessageBox.Show(
                    LocalizationManager.GetString("InstallFinishedQuestionFormat", Name),
                    LocalizationManager.GetString("InstallCompleteTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
                await PlayAsync();
        }

        private static string LocalizeLaunchError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return LocalizationManager.GetString("LaunchErrorTitle");
            if (error == "Эта игра уже запущена.")
                return LocalizationManager.GetString("LaunchAlreadyRunning");
            if (error.StartsWith("Нельзя запустить игру, пока работает другая.", StringComparison.Ordinal))
                return LocalizationManager.GetString("LaunchBlockedByRunningGame");
            if (error.StartsWith("Не удалось определить exe-файл игры.", StringComparison.Ordinal))
                return LocalizationManager.GetString("LaunchExecutableNotFound");
            if (error.StartsWith("Не удалось запустить игру: ", StringComparison.Ordinal))
                return LocalizationManager.GetString("LaunchFailedFormat", error["Не удалось запустить игру: ".Length..]);
            return error;
        }

        private async Task PlayAsync()
        {
            IsBusy = true;
            try
            {
                var result = await _gameLauncher.LaunchAsync(_definition, _installation);
                if (!result.Success)
                {
                    MessageBox.Show(LocalizeLaunchError(result.ErrorMessage),
                        LocalizationManager.GetString("LaunchErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                NotifyRunningChanged();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task PauseAsync()
        {
            // Let GameManager mark the state as paused before the engine unwinds.
            // Cancelling from the UI first used to make the downloader treat the
            // operation as a hard cancel and drop its resume data.
            IsBusy = true;
            try
            {
                await _gameManager.PauseAsync(Id);
                UpdateInstallation(_gameManager.GetInstallation(Id));

                // Show a frozen snapshot (no live speed) while the download is paused.
                Progress = BuildPersistedProgress() ?? Progress;

                // Mark this operation as user-cancelled so the pending Install/Resume
                // continuation treats it as a deliberate pause and does not show an error.
                _cts?.Cancel();
            }
            catch { /* keep the card usable */ }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ResumeAsync()
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            var progressReporter = new Progress<DownloadProgress>(p =>
            {
                if (_isRemovingDownload) return;
                if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
                    d.BeginInvoke(() => Progress = p);
                else
                    Progress = p;
            });

            try
            {
                var result = await _gameManager.ResumeAsync(Id, progressReporter, _cts.Token);
                UpdateInstallation(_gameManager.GetInstallation(Id));

                if (_isRemovingDownload || State == InstallState.NotInstalled)
                {
                    Progress = null;
                    return;
                }

                if (result.Success)
                {
                    Progress = null;
                    _notifications.Show(
                        LocalizationManager.GetString("InstallCompleteTitle"),
                        LocalizationManager.GetString("ToastInstallDoneFormat", Name));
                }
                else
                {
                    // Show whatever is already on disk instead of an empty card, and keep
                    // the Continue button usable (GameManager leaves the state Paused).
                    Progress = BuildPersistedProgress() ?? Progress;

                    // A user pause cancels _cts (see PauseAsync), so this only fires for
                    // a genuine resume failure — surface the reason instead of failing
                    // silently and leaving the card stuck on "Continue".
                    if (!_cts.IsCancellationRequested)
                    {
                        MessageBox.Show(LocalizationManager.GetString("InstallFailedFormat",
                                result.ErrorMessage ?? LocalizationManager.GetString("InstallUnknown")),
                            LocalizationManager.GetString("InstallErrorTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RetryDownloadAsync()
        {
            if (HasResumableSource)
            {
                await ResumeAsync();
                return;
            }

            await InstallAsync();
        }

        private void DismissCompletedDownload()
        {
            _gameManager.DismissCompletedDownload(Id);
            UpdateInstallation(_gameManager.GetInstallation(Id));
        }

        private void OpenDownloadFolder()
        {
            try
            {
                var candidatePaths = new[]
                {
                    IsInstalled ? InstallPath : null,
                    DownloadPathDisplay,
                    InstallPath,
                    !string.IsNullOrWhiteSpace(DownloadPathDisplay) ? Path.GetDirectoryName(DownloadPathDisplay) : null,
                    !string.IsNullOrWhiteSpace(InstallPath) ? Path.GetDirectoryName(InstallPath) : null,
                    _settingsService.Settings.DefaultInstallPath,
                    PathHelper.DefaultLibraryPath
                };

                var existingFolder = candidatePaths
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));

                if (!string.IsNullOrWhiteSpace(existingFolder))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = existingFolder,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // Ignore shell errors if Explorer fails to open.
            }
        }

        private async Task RemoveDownloadAsync()
        {
            var answer = MessageBox.Show(
                LocalizationManager.GetString("RemoveDownloadQuestionFormat", Name),
                LocalizationManager.GetString("RemoveDownloadTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            SetRemovingDownload(true);
            try
            {
                _cts?.Cancel();
                await _gameManager.RemoveDownloadAsync(Id);
                Progress = null;
                UpdateInstallation(_gameManager.GetInstallation(Id));
            }
            catch
            {
                UpdateInstallation(_gameManager.GetInstallation(Id));
            }
            finally
            {
                SetRemovingDownload(false);
            }
        }

        private async Task VerifyAsync()
        {
            IsBusy = true;
            try
            {
                var progressReporter = new Progress<DownloadProgress>(p =>
                {
                    Application.Current?.Dispatcher?.Invoke(() => Progress = p);
                });

                var result = await _gameManager.VerifyAsync(Id, progressReporter);
                Progress = null;

                MessageBox.Show(result.Summary,
                    LocalizationManager.GetString(result.Passed ? "VerifyPassedTitle" : "VerifyFailedTitle"),
                    MessageBoxButton.OK,
                    result.Passed ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task UpdateAsync()
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            var progressReporter = new Progress<DownloadProgress>(p =>
            {
                Application.Current?.Dispatcher?.Invoke(() => Progress = p);
            });

            try
            {
                var result = await _gameManager.UpdateAsync(Id, progressReporter, _cts.Token);
                UpdateInstallation(_gameManager.GetInstallation(Id));
                Progress = null;

                if (!result.Success)
                {
                    MessageBox.Show(LocalizationManager.GetString("UpdateFailedFormat",
                            result.ErrorMessage ?? LocalizationManager.GetString("InstallUnknown")),
                        LocalizationManager.GetString("UpdateErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task UninstallAsync()
        {
            var confirm = MessageBox.Show(
                LocalizationManager.GetString("UninstallConfirmFormat", Name),
                LocalizationManager.GetString("UninstallTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            try
            {
                var result = await _gameManager.UninstallAsync(Id);
                if (result.Success)
                {
                    UpdateInstallation(new GameInstallation { GameId = Id, State = InstallState.NotInstalled });
                    Progress = null;
                }
                else
                {
                    MessageBox.Show(LocalizationManager.GetString("UninstallFailedFormat",
                            result.ErrorMessage ?? LocalizationManager.GetString("InstallUnknown")),
                        LocalizationManager.GetString("UninstallErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenFolder()
        {
            if (Directory.Exists(_installation.InstallPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _installation.InstallPath,
                    UseShellExecute = true
                });
            }
        }
    }
}
