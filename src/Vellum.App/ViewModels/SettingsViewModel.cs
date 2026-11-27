using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Vellum.App.Services;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.App.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private const string WindowsRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string WindowsRunValueName = "Vellum";

        private readonly ISettingsService _settingsService;
        private readonly IGameCatalog _catalog;
        private readonly ILauncherUpdateService? _launcherUpdates;
        private readonly IGameUpdateService? _gameUpdates;
        private bool _isLoadingSettings;
        private CancellationTokenSource? _statusCts;

        public SettingsViewModel(ISettingsService settingsService, IGameCatalog catalog,
            IGameUpdateService? gameUpdates = null, ILauncherUpdateService? launcherUpdates = null)
        {
            _settingsService = settingsService;
            _catalog = catalog;
            _gameUpdates = gameUpdates;
            _launcherUpdates = launcherUpdates;
            if (_launcherUpdates != null)
                _launcherUpdates.StatusChanged += OnLauncherUpdateStatusChanged;

            BrowseInstallPathCommand = new RelayCommand(BrowseInstallPath);
            OpenLibraryFolderCommand = new RelayCommand(OpenLibraryFolder);
            OpenAppDataFolderCommand = new RelayCommand(OpenAppDataFolder);
            SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsExplicitAsync);
            ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);
            ClearTempCacheCommand = new AsyncRelayCommand(ClearTempCacheAsync);
            AddCatalogUrlCommand = new AsyncRelayCommand(AddCatalogUrlAsync);
            BrowseCatalogFileCommand = new RelayCommand(BrowseCatalogFile);
            RemoveCatalogUrlCommand = new AsyncRelayCommand(RemoveCatalogUrlAsync);
            SelectAccentCommand = new RelayCommand(SelectAccent);
            SetDownloadLimitPresetCommand = new RelayCommand(SetDownloadLimitPreset);
            SetUploadLimitPresetCommand = new RelayCommand(SetUploadLimitPreset);
            DismissStatusCommand = new RelayCommand(() => IsStatusVisible = false);
            SelectUpdateChannelCommand = new RelayCommand(p => UpdateChannel = p?.ToString() == "beta" ? "beta" : "stable");
            SetUpdateLaunchBehaviorCommand = new RelayCommand(p => UpdateLaunchBehavior = p?.ToString() ?? "ask");
            CheckLauncherUpdateCommand = new AsyncRelayCommand(CheckLauncherUpdateAsync, () => !IsLauncherUpdateBusy);
            DownloadLauncherUpdateCommand = new AsyncRelayCommand(DownloadLauncherUpdateAsync,
                () => CanDownloadLauncherUpdate);
            RestartForLauncherUpdateCommand = new AsyncRelayCommand(RestartForLauncherUpdateAsync,
                () => CanRestartLauncherUpdate);

            LocalizationManager.LanguageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(LauncherVersionText));
                RefreshStorageInfo();
                RefreshCacheSize();
            };

            LoadSettings();
        }

        public RelayCommand BrowseInstallPathCommand { get; }
        public RelayCommand OpenLibraryFolderCommand { get; }
        public RelayCommand OpenAppDataFolderCommand { get; }
        public AsyncRelayCommand SaveSettingsCommand { get; }
        public AsyncRelayCommand ResetSettingsCommand { get; }
        public AsyncRelayCommand ClearTempCacheCommand { get; }
        public AsyncRelayCommand AddCatalogUrlCommand { get; }
        public RelayCommand BrowseCatalogFileCommand { get; }
        public AsyncRelayCommand RemoveCatalogUrlCommand { get; }
        public RelayCommand SelectAccentCommand { get; }
        public RelayCommand SetDownloadLimitPresetCommand { get; }
        public RelayCommand SetUploadLimitPresetCommand { get; }
        public RelayCommand DismissStatusCommand { get; }
        public RelayCommand SelectUpdateChannelCommand { get; }
        public RelayCommand SetUpdateLaunchBehaviorCommand { get; }
        public AsyncRelayCommand CheckLauncherUpdateCommand { get; }
        public AsyncRelayCommand DownloadLauncherUpdateCommand { get; }
        public AsyncRelayCommand RestartForLauncherUpdateCommand { get; }

        public event EventHandler? SettingsChanged;

        /// <summary>Version of the launcher build, taken from the assembly metadata.</summary>
        public string LauncherVersion { get; } = ResolveLauncherVersion();

        public string LauncherVersionText =>
            LocalizationManager.GetString("SettingsLauncherVersionFormat", LauncherVersion);

        private static string ResolveLauncherVersion()
        {
            var assembly = typeof(App).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var metadataIndex = informational.IndexOf('+');
                return metadataIndex > 0 ? informational[..metadataIndex] : informational;
            }

            var version = assembly.GetName().Version;
            return version == null
                ? "1.0"
                : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }

        private string _defaultInstallPath = PathHelper.DefaultLibraryPath;
        public string DefaultInstallPath
        {
            get => _defaultInstallPath;
            set
            {
                if (SetProperty(ref _defaultInstallPath, value))
                {
                    RefreshStorageInfo();
                    AutoSaveIfReady();
                }
            }
        }

        private int _maxDownloadSpeedMbps;
        public int MaxDownloadSpeedMbps
        {
            get => _maxDownloadSpeedMbps;
            set
            {
                var clamped = Math.Max(0, value);
                if (SetProperty(ref _maxDownloadSpeedMbps, clamped))
                {
                    OnPropertyChanged(nameof(IsDownloadUnlimited));
                    AutoSaveIfReady();
                }
            }
        }

        public bool IsDownloadUnlimited => _maxDownloadSpeedMbps == 0;

        private int _maxUploadSpeedMbps;
        public int MaxUploadSpeedMbps
        {
            get => _maxUploadSpeedMbps;
            set
            {
                var clamped = Math.Max(0, value);
                if (SetProperty(ref _maxUploadSpeedMbps, clamped))
                {
                    OnPropertyChanged(nameof(IsUploadUnlimited));
                    AutoSaveIfReady();
                }
            }
        }

        public bool IsUploadUnlimited => _maxUploadSpeedMbps == 0;

        private bool _allowSeeding;
        public bool AllowSeeding
        {
            get => _allowSeeding;
            set
            {
                if (SetProperty(ref _allowSeeding, value))
                    AutoSaveIfReady();
            }
        }

        private bool _autoCheckUpdates = true;
        public bool AutoCheckUpdates
        {
            get => _autoCheckUpdates;
            set
            {
                if (SetProperty(ref _autoCheckUpdates, value))
                    AutoSaveIfReady();
            }
        }

        private bool _autoUpdateGames;
        public bool AutoUpdateGames
        {
            get => _autoUpdateGames;
            set => SetPropertyAndSave(ref _autoUpdateGames, value);
        }

        private int _updateCheckIntervalMinutes = 30;
        public int UpdateCheckIntervalMinutes
        {
            get => _updateCheckIntervalMinutes;
            set => SetPropertyAndSave(ref _updateCheckIntervalMinutes, Math.Clamp(value, 0, 1440));
        }

        private int _maxConcurrentUpdateFiles = 3;
        public int MaxConcurrentUpdateFiles
        {
            get => _maxConcurrentUpdateFiles;
            set => SetPropertyAndSave(ref _maxConcurrentUpdateFiles, Math.Clamp(value, 1, 8));
        }

        private int _maxConcurrentDownloads = 1;
        public int MaxConcurrentDownloads
        {
            get => _maxConcurrentDownloads;
            set => SetPropertyAndSave(ref _maxConcurrentDownloads, Math.Clamp(value, 1, 5));
        }

        private string _updateManifestBaseUrl = string.Empty;
        public string UpdateManifestBaseUrl
        {
            get => _updateManifestBaseUrl;
            set => SetPropertyAndSave(ref _updateManifestBaseUrl, value?.Trim() ?? string.Empty);
        }

        private string _updateChannel = "stable";
        public string UpdateChannel
        {
            get => _updateChannel;
            set
            {
                var normalized = string.Equals(value, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";
                if (SetProperty(ref _updateChannel, normalized))
                {
                    OnPropertyChanged(nameof(IsUpdateChannelStable));
                    OnPropertyChanged(nameof(IsUpdateChannelBeta));
                    AutoSaveIfReady();
                }
            }
        }

        public bool IsUpdateChannelStable => UpdateChannel == "stable";
        public bool IsUpdateChannelBeta => UpdateChannel == "beta";

        private string _updateLaunchBehavior = "ask";
        public string UpdateLaunchBehavior
        {
            get => _updateLaunchBehavior;
            set
            {
                var normalized = value is "block" or "allow" ? value : "ask";
                if (SetProperty(ref _updateLaunchBehavior, normalized))
                {
                    OnPropertyChanged(nameof(IsUpdateLaunchAsk));
                    OnPropertyChanged(nameof(IsUpdateLaunchBlock));
                    OnPropertyChanged(nameof(IsUpdateLaunchAllow));
                    AutoSaveIfReady();
                }
            }
        }

        public bool IsUpdateLaunchAsk => UpdateLaunchBehavior == "ask";
        public bool IsUpdateLaunchBlock => UpdateLaunchBehavior == "block";
        public bool IsUpdateLaunchAllow => UpdateLaunchBehavior == "allow";

        private bool _autoCheckLauncherUpdates = true;
        public bool AutoCheckLauncherUpdates
        {
            get => _autoCheckLauncherUpdates;
            set => SetPropertyAndSave(ref _autoCheckLauncherUpdates, value);
        }

        private bool _autoDownloadLauncherUpdates = true;
        public bool AutoDownloadLauncherUpdates
        {
            get => _autoDownloadLauncherUpdates;
            set => SetPropertyAndSave(ref _autoDownloadLauncherUpdates, value);
        }

        private string _launcherUpdateManifestUrl = string.Empty;
        public string LauncherUpdateManifestUrl
        {
            get => _launcherUpdateManifestUrl;
            set
            {
                if (SetProperty(ref _launcherUpdateManifestUrl, value?.Trim() ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasLauncherUpdateSource));
                    AutoSaveIfReady();
                }
            }
        }

        private void SetPropertyAndSave<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
        {
            if (SetProperty(ref field, value, property))
                AutoSaveIfReady();
        }

        public bool HasLauncherUpdateSource => !string.IsNullOrWhiteSpace(LauncherUpdateManifestUrl);

        private string _launcherUpdateStatusText = string.Empty;
        public string LauncherUpdateStatusText
        {
            get => _launcherUpdateStatusText;
            private set => SetProperty(ref _launcherUpdateStatusText, value);
        }

        private bool _hasLauncherUpdate;
        public bool HasLauncherUpdate
        {
            get => _hasLauncherUpdate;
            private set => SetProperty(ref _hasLauncherUpdate, value);
        }

        private bool _isLauncherUpdateBusy;
        public bool IsLauncherUpdateBusy
        {
            get => _isLauncherUpdateBusy;
            private set
            {
                if (!SetProperty(ref _isLauncherUpdateBusy, value))
                    return;
                OnPropertyChanged(nameof(CanDownloadLauncherUpdate));
                OnPropertyChanged(nameof(CanRestartLauncherUpdate));
                CheckLauncherUpdateCommand.RaiseCanExecuteChanged();
                DownloadLauncherUpdateCommand.RaiseCanExecuteChanged();
                RestartForLauncherUpdateCommand.RaiseCanExecuteChanged();
            }
        }

        private double _launcherUpdateProgress;
        public double LauncherUpdateProgress
        {
            get => _launcherUpdateProgress;
            private set => SetProperty(ref _launcherUpdateProgress, value);
        }

        private string _launcherUpdateSpeedText = string.Empty;
        public string LauncherUpdateSpeedText
        {
            get => _launcherUpdateSpeedText;
            private set => SetProperty(ref _launcherUpdateSpeedText, value);
        }

        private bool _canDownloadLauncherUpdate;
        public bool CanDownloadLauncherUpdate
        {
            get => _canDownloadLauncherUpdate;
            private set
            {
                if (SetProperty(ref _canDownloadLauncherUpdate, value))
                    DownloadLauncherUpdateCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _canRestartLauncherUpdate;
        public bool CanRestartLauncherUpdate
        {
            get => _canRestartLauncherUpdate;
            private set
            {
                if (SetProperty(ref _canRestartLauncherUpdate, value))
                    RestartForLauncherUpdateCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _hasLauncherUpdateError;
        public bool HasLauncherUpdateError
        {
            get => _hasLauncherUpdateError;
            private set => SetProperty(ref _hasLauncherUpdateError, value);
        }

        private bool _launchWithWindows;

        public bool LaunchWithWindows
        {
            get => _launchWithWindows;
            set
            {
                if (SetProperty(ref _launchWithWindows, value))
                {
                    if (!_isLoadingSettings)
                        ApplyWindowsStartupSetting(value);
                    AutoSaveIfReady();
                }
            }
        }

        private bool _minimizeToTray = true;
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set
            {
                if (SetProperty(ref _minimizeToTray, value))
                    AutoSaveIfReady();
            }
        }

        private bool _minimizeOnGameLaunch = true;
        public bool MinimizeOnGameLaunch
        {
            get => _minimizeOnGameLaunch;
            set
            {
                if (SetProperty(ref _minimizeOnGameLaunch, value))
                    AutoSaveIfReady();
            }
        }

        private bool _restoreOnGameExit = true;
        public bool RestoreOnGameExit
        {
            get => _restoreOnGameExit;
            set
            {
                if (SetProperty(ref _restoreOnGameExit, value))
                    AutoSaveIfReady();
            }
        }

        private bool _allowMultipleGames;
        public bool AllowMultipleGames
        {
            get => _allowMultipleGames;
            set
            {
                if (SetProperty(ref _allowMultipleGames, value))
                    AutoSaveIfReady();
            }
        }

        private bool _preferPortable = true;
        public bool PreferPortable
        {
            get => _preferPortable;
            set
            {
                if (SetProperty(ref _preferPortable, value))
                    AutoSaveIfReady();
            }
        }

        private bool _askInstallFolderOnEachInstall;
        public bool AskInstallFolderOnEachInstall
        {
            get => _askInstallFolderOnEachInstall;
            set
            {
                if (SetProperty(ref _askInstallFolderOnEachInstall, value))
                {
                    OnPropertyChanged(nameof(IsInstallPathEditable));
                    AutoSaveIfReady();
                }
            }
        }

        /// <summary>РџСѓС‚СЊ РјРѕР¶РЅРѕ СЂРµРґР°РєС‚РёСЂРѕРІР°С‚СЊ С‚РѕР»СЊРєРѕ РєРѕРіРґР° РІС‹Р±РѕСЂ РїР°РїРєРё РѕС‚РєР»СЋС‡С‘РЅ.</summary>
        public bool IsInstallPathEditable => !_askInstallFolderOnEachInstall;

        private string _selectedAccent = "White";
        public string SelectedAccent
        {
            get => _selectedAccent;
            set
            {
                var normalized = AccentOption.Find(value).Code;
                if (SetProperty(ref _selectedAccent, normalized))
                {
                    AccentOption.Apply(normalized);
                    AutoSaveIfReady();
                }
                else
                {
                    AccentOption.Apply(normalized);
                }
            }
        }

        public IReadOnlyList<AccentOption> AccentOptions => AccentOption.All;

        private string _selectedLanguage = LocalizationManager.GetSystemLanguage();
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                var language = value is "ru" or "uk" or "en"
                    ? value
                    : LocalizationManager.GetSystemLanguage();
                if (SetProperty(ref _selectedLanguage, language))
                {
                    LocalizationManager.SetLanguage(language);
                    if (!_isLoadingSettings)
                    {
                        _settingsService.Settings.Language = language;
                        _settingsService.Settings.IsLanguageManuallySelected = true;
                        AutoSaveIfReady();
                    }
                }
            }
        }

        public ObservableCollection<LanguageOption> LanguageOptions { get; } = new()
        {
            new("ru", "Р СѓСЃСЃРєРёР№"),
            new("uk", "РЈРєСЂР°С—РЅСЃСЊРєР°"),
            new("en", "English")
        };

        private string _newCatalogUrl = string.Empty;
        public string NewCatalogUrl
        {
            get => _newCatalogUrl;
            set => SetProperty(ref _newCatalogUrl, value);
        }

        public ObservableCollection<string> AdditionalCatalogUrls { get; } = new();

        private string _driveUsageSummary = string.Empty;
        public string DriveUsageSummary
        {
            get => _driveUsageSummary;
            private set => SetProperty(ref _driveUsageSummary, value);
        }

        private double _driveUsedPercent;
        public double DriveUsedPercent
        {
            get => _driveUsedPercent;
            private set => SetProperty(ref _driveUsedPercent, value);
        }

        private bool _isDriveSpaceLow;
        public bool IsDriveSpaceLow
        {
            get => _isDriveSpaceLow;
            private set => SetProperty(ref _isDriveSpaceLow, value);
        }

        private string _cacheSizeSummary = string.Empty;
        public string CacheSizeSummary
        {
            get => _cacheSizeSummary;
            private set => SetProperty(ref _cacheSizeSummary, value);
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        private bool _isStatusVisible;
        public bool IsStatusVisible
        {
            get => _isStatusVisible;
            private set => SetProperty(ref _isStatusVisible, value);
        }

        private bool _isStatusError;
        public bool IsStatusError
        {
            get => _isStatusError;
            private set => SetProperty(ref _isStatusError, value);
        }

        public void LoadSettings()
        {
            var s = _settingsService.Settings;
            _isLoadingSettings = true;
            try
            {
                DefaultInstallPath = string.IsNullOrWhiteSpace(s.DefaultInstallPath)
                    ? PathHelper.DefaultLibraryPath
                    : s.DefaultInstallPath;

                MaxDownloadSpeedMbps = (int)(s.MaxDownloadSpeedBps / (1024 * 1024));
                MaxUploadSpeedMbps = (int)(s.MaxUploadSpeedBps / (1024 * 1024));
                AllowSeeding = s.AllowSeeding;
                AutoCheckUpdates = s.AutoCheckUpdates;
                AutoUpdateGames = s.AutoUpdateGames;
                UpdateCheckIntervalMinutes = s.UpdateCheckIntervalMinutes;
                MaxConcurrentUpdateFiles = s.MaxConcurrentUpdateFiles;
                MaxConcurrentDownloads = s.MaxConcurrentDownloads;
                UpdateManifestBaseUrl = s.UpdateManifestBaseUrl;
                UpdateChannel = s.UpdateChannel;
                UpdateLaunchBehavior = s.UpdateLaunchBehavior;
                AutoCheckLauncherUpdates = s.AutoCheckLauncherUpdates;
                AutoDownloadLauncherUpdates = s.AutoDownloadLauncherUpdates;
                LauncherUpdateManifestUrl = s.LauncherUpdateManifestUrl;
                OnPropertyChanged(nameof(HasLauncherUpdateSource));
                LaunchWithWindows = s.LaunchWithWindows;
                MinimizeToTray = s.MinimizeToTray;
                MinimizeOnGameLaunch = s.MinimizeOnGameLaunch;
                RestoreOnGameExit = s.RestoreOnGameExit;
                AllowMultipleGames = s.AllowMultipleGames;
                PreferPortable = s.PreferPortable;
                AskInstallFolderOnEachInstall = s.AskInstallFolderOnEachInstall;
                SelectedAccent = s.AccentColor;

                var language = s.IsLanguageManuallySelected && (s.Language is "ru" or "uk" or "en")
                    ? s.Language
                    : (!string.IsNullOrWhiteSpace(s.Language) && (s.Language is "ru" or "uk" or "en"))
                        ? s.Language
                        : LocalizationManager.GetSystemLanguage();
                SelectedLanguage = language;
                LocalizationManager.SetLanguage(language);
                s.Language = language;
                s.IsLanguageManuallySelected = true;

                AdditionalCatalogUrls.Clear();
                foreach (var url in s.AdditionalCatalogUrls)
                    AdditionalCatalogUrls.Add(url);
            }
            finally
            {
                _isLoadingSettings = false;
            }

            RefreshStorageInfo();
            RefreshCacheSize();
        }

        private void SelectAccent(object? parameter)
        {
            if (parameter is AccentOption option)
                SelectedAccent = option.Code;
            else if (parameter is string code && !string.IsNullOrWhiteSpace(code))
                SelectedAccent = code;
        }

        private void SetDownloadLimitPreset(object? parameter)
        {
            if (parameter != null && int.TryParse(parameter.ToString(), out var value))
                MaxDownloadSpeedMbps = value;
        }

        private void SetUploadLimitPreset(object? parameter)
        {
            if (parameter != null && int.TryParse(parameter.ToString(), out var value))
                MaxUploadSpeedMbps = value;
        }

        private void BrowseInstallPath()
        {
            var dialog = new OpenFolderDialog
            {
                Title = LocalizationManager.GetString("SettingsBrowseFolderTitle"),
                InitialDirectory = Directory.Exists(DefaultInstallPath) ? DefaultInstallPath : @"C:\"
            };

            if (dialog.ShowDialog() == true)
            {
                DefaultInstallPath = PathHelper.GetLibraryRoot(dialog.FolderName);
            }
        }

        private void OpenLibraryFolder()
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(DefaultInstallPath)
                    ? PathHelper.DefaultLibraryPath
                    : DefaultInstallPath;
                PathHelper.EnsureDirectory(target);
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, isError: true);
            }
        }

        private void OpenAppDataFolder()
        {
            try
            {
                PathHelper.EnsureDirectory(PathHelper.AppDataDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = PathHelper.AppDataDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, isError: true);
            }
        }

        public void RefreshStorageInfo()
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(DefaultInstallPath)
                    ? PathHelper.DefaultLibraryPath
                    : DefaultInstallPath;
                var fullPath = Path.GetFullPath(target);
                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    DriveUsageSummary = LocalizationManager.GetString("SettingsDriveUnavailable");
                    DriveUsedPercent = 0;
                    IsDriveSpaceLow = false;
                    return;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    DriveUsageSummary = LocalizationManager.GetString("SettingsDriveUnavailable");
                    DriveUsedPercent = 0;
                    IsDriveSpaceLow = false;
                    return;
                }

                var freeBytes = drive.AvailableFreeSpace;
                var totalBytes = drive.TotalSize;
                var usedBytes = Math.Max(0, totalBytes - freeBytes);

                DriveUsedPercent = Math.Clamp((double)usedBytes / totalBytes * 100.0, 0.0, 100.0);
                IsDriveSpaceLow = freeBytes < 10L * 1024 * 1024 * 1024;
                DriveUsageSummary = LocalizationManager.GetString(
                    "SettingsDriveFreeFormat",
                    root.TrimEnd('\\'),
                    PathHelper.FormatBytes(freeBytes),
                    PathHelper.FormatBytes(totalBytes));
            }
            catch
            {
                DriveUsageSummary = LocalizationManager.GetString("SettingsDriveUnavailable");
                DriveUsedPercent = 0;
                IsDriveSpaceLow = false;
            }
        }

        public void RefreshCacheSize()
        {
            try
            {
                long bytes = GetDirectorySizeSafe(PathHelper.LegacyTempDir)
                           + GetDirectorySizeSafe(PathHelper.TorrentDataDir)
                           + GetDirectorySizeSafe(PathHelper.ArtworkCacheDir)
                           + GetDirectorySizeSafe(PathHelper.SteamMetadataCacheDir);
                CacheSizeSummary = LocalizationManager.GetString(
                    "SettingsCacheSizeFormat",
                    PathHelper.FormatBytes(bytes));
            }
            catch (Exception ex)
            {
                AppLog.Debug("SettingsViewModel", $"Could not calculate the cache size: {ex.Message}");
                CacheSizeSummary = LocalizationManager.GetString("SettingsCacheSizeFormat", "0 B");
            }
        }

        private static long GetDirectorySizeSafe(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return 0;

            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { }
                }
            }
            catch { }

            return total;
        }

        private void SyncToSettingsModel()
        {
            var s = _settingsService.Settings;
            s.DefaultInstallPath = DefaultInstallPath;
            s.MaxDownloadSpeedBps = (long)MaxDownloadSpeedMbps * 1024 * 1024;
            s.MaxUploadSpeedBps = (long)MaxUploadSpeedMbps * 1024 * 1024;
            s.AllowSeeding = AllowSeeding;
            s.AutoCheckUpdates = AutoCheckUpdates;
            s.AutoUpdateGames = AutoUpdateGames;
            s.UpdateCheckIntervalMinutes = UpdateCheckIntervalMinutes;
            s.MaxConcurrentUpdateFiles = MaxConcurrentUpdateFiles;
            s.MaxConcurrentDownloads = MaxConcurrentDownloads;
            s.UpdateManifestBaseUrl = UpdateManifestBaseUrl;
            s.UpdateChannel = UpdateChannel;
            s.UpdateLaunchBehavior = UpdateLaunchBehavior;
            s.AutoCheckLauncherUpdates = AutoCheckLauncherUpdates;
            s.AutoDownloadLauncherUpdates = AutoDownloadLauncherUpdates;
            s.LauncherUpdateManifestUrl = LauncherUpdateManifestUrl;
            s.LaunchWithWindows = LaunchWithWindows;
            s.MinimizeToTray = MinimizeToTray;
            s.MinimizeOnGameLaunch = MinimizeOnGameLaunch;
            s.RestoreOnGameExit = RestoreOnGameExit;
            s.AllowMultipleGames = AllowMultipleGames;
            s.PreferPortable = PreferPortable;
            s.AskInstallFolderOnEachInstall = AskInstallFolderOnEachInstall;
            s.AccentColor = SelectedAccent;
            s.Language = SelectedLanguage;
            s.IsLanguageManuallySelected = true;

            s.AdditionalCatalogUrls.Clear();
            s.AdditionalCatalogUrls.AddRange(AdditionalCatalogUrls);
        }

        private void AutoSaveIfReady()
        {
            if (_isLoadingSettings)
                return;

            SyncToSettingsModel();
            _ = _settingsService.SaveAsync();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private async Task SaveSettingsExplicitAsync()
        {
            SyncToSettingsModel();
            await _settingsService.SaveAsync();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            RefreshStorageInfo();
            RefreshCacheSize();
            ShowStatus(LocalizationManager.GetString("SettingsSaved"), isError: false);
        }

        private async Task ResetSettingsAsync()
        {
            var confirm = MessageBox.Show(
                LocalizationManager.GetString("SettingsResetConfirmQuestion"),
                LocalizationManager.GetString("NavSettings"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            _settingsService.Reset();
            _settingsService.Settings.DefaultInstallPath = PathHelper.DefaultLibraryPath;
            ApplyWindowsStartupSetting(false);
            await _settingsService.SaveAsync();
            LoadSettings();
            await _catalog.RefreshAsync();
            ShowStatus(LocalizationManager.GetString("SettingsResetDone"), isError: false);
        }

        private Task ClearTempCacheAsync()
        {
            try
            {
                string? firstError = null;
                DeleteDirectorySafe(PathHelper.LegacyTempDir, ref firstError);
                DeleteDirectorySafe(PathHelper.TorrentDataDir, ref firstError);
                DeleteDirectorySafe(PathHelper.ArtworkCacheDir, ref firstError);
                DeleteDirectorySafe(PathHelper.SteamMetadataCacheDir, ref firstError);

                PathHelper.EnsureDirectory(PathHelper.LegacyTempDir);
                PathHelper.EnsureDirectory(PathHelper.TorrentDataDir);

                RefreshCacheSize();
                if (firstError != null)
                {
                    ShowStatus(LocalizationManager.GetString("SettingsCacheClearFailedFormat", firstError), isError: true);
                    return Task.CompletedTask;
                }

                ShowStatus(LocalizationManager.GetString("SettingsCacheCleared"), isError: false);
            }
            catch (Exception ex)
            {
                ShowStatus(LocalizationManager.GetString("SettingsCacheClearFailedFormat", ex.Message), isError: true);
            }
            return Task.CompletedTask;
        }

        private static void DeleteDirectorySafe(string path, ref string? firstError)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        private void BrowseCatalogFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = LocalizationManager.GetString("SettingsBrowseCatalogTitle"),
                Filter = "JSON Catalog (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                NewCatalogUrl = dialog.FileName;
            }
        }

        private async Task AddCatalogUrlAsync()
        {
            if (string.IsNullOrWhiteSpace(NewCatalogUrl))
                return;

            var url = NewCatalogUrl.Trim();
            var isValidHttp = Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
                && (parsedUri.Scheme == Uri.UriSchemeHttp || parsedUri.Scheme == Uri.UriSchemeHttps);
            var isValidFile = File.Exists(url);

            if (!isValidHttp && !isValidFile)
            {
                ShowStatus(LocalizationManager.GetString("SettingsSourceInvalid"), isError: true);
                return;
            }

            if (!AdditionalCatalogUrls.Contains(url))
            {
                AdditionalCatalogUrls.Add(url);
                NewCatalogUrl = string.Empty;
                SyncToSettingsModel();
                await _settingsService.SaveAsync();
                await _catalog.RefreshAsync();
                ShowStatus(LocalizationManager.GetString("SettingsSourceAdded"), isError: false);
            }
            else
            {
                NewCatalogUrl = string.Empty;
            }
        }

        private async Task RemoveCatalogUrlAsync(object? param)
        {
            if (param is string url && AdditionalCatalogUrls.Remove(url))
            {
                SyncToSettingsModel();
                await _settingsService.SaveAsync();
                await _catalog.RefreshAsync();
                ShowStatus(LocalizationManager.GetString("SettingsSourceRemoved"), isError: false);
            }
        }

        private static void ApplyWindowsStartupSetting(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsRunRegistryPath, writable: true);
                if (key == null)
                    return;

                if (enable)
                {
                    var exePath = Environment.ProcessPath;
                    var bootstrapper = Path.Combine(AppContext.BaseDirectory, "Bootstrapper", "Vellum.Bootstrapper.exe");
                    if (File.Exists(bootstrapper))
                        exePath = bootstrapper;
                    if (!string.IsNullOrWhiteSpace(exePath))
                        key.SetValue(WindowsRunValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(WindowsRunValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // Best effort; do not fail if registry access is restricted.
            }
        }

        public async Task InitializeUpdatesAsync()
        {
            if (_launcherUpdates == null)
                return;

            var result = await _launcherUpdates.ConsumeBootstrapperResultAsync();
            if (result != null && !result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    ShowStatus(result.ErrorMessage!, isError: true));
            }

            if (AutoCheckLauncherUpdates && HasLauncherUpdateSource)
                await CheckLauncherUpdateAsync();
        }

        private async Task CheckLauncherUpdateAsync()
        {
            if (_launcherUpdates == null || IsLauncherUpdateBusy)
                return;
            if (!HasLauncherUpdateSource)
            {
                ShowStatus(LocalizationManager.GetStringOrFallback(
                    "SettingsLauncherUpdateNoSource", "Set a launcher update manifest URL first."), isError: true);
                return;
            }

            IsLauncherUpdateBusy = true;
            try
            {
                var status = await _launcherUpdates.CheckForUpdatesAsync(LauncherVersion);
                ApplyLauncherStatus(status);
            }
            finally
            {
                IsLauncherUpdateBusy = false;
            }
        }

        private async Task DownloadLauncherUpdateAsync()
        {
            if (_launcherUpdates == null || IsLauncherUpdateBusy)
                return;

            IsLauncherUpdateBusy = true;
            try
            {
                var result = await _launcherUpdates.DownloadAsync(LauncherVersion);
                if (!result.Success && !result.CancelledByUser)
                    ShowStatus(result.ErrorMessage ?? "Launcher update download failed.", isError: true);
            }
            finally
            {
                IsLauncherUpdateBusy = false;
                ApplyLauncherStatus(_launcherUpdates.Status);
            }
        }

        private async Task RestartForLauncherUpdateAsync()
        {
            if (_launcherUpdates == null || IsLauncherUpdateBusy)
                return;

            var confirm = MessageBox.Show(
                LocalizationManager.GetStringOrFallback(
                    "SettingsLauncherUpdateRestartQuestion",
                    "Close Vellum and install the update now? The launcher will restart automatically."),
                LocalizationManager.GetStringOrFallback("UpdateErrorTitle", "Update"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;

            IsLauncherUpdateBusy = true;
            try
            {
                var result = await _launcherUpdates.PrepareRestartAsync(
                    Environment.ProcessId, LauncherVersion);
                if (!result.Success)
                {
                    ShowStatus(result.ErrorMessage ?? "Could not start the updater.", isError: true);
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
            }
            finally
            {
                IsLauncherUpdateBusy = false;
            }
        }

        private void OnLauncherUpdateStatusChanged(object? sender, LauncherUpdateStatus status)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => ApplyLauncherStatus(status));
                return;
            }
            ApplyLauncherStatus(status);
        }

        private void ApplyLauncherStatus(LauncherUpdateStatus status)
        {
            HasLauncherUpdate = status.Phase is LauncherUpdatePhase.UpdateAvailable
                or LauncherUpdatePhase.Downloading or LauncherUpdatePhase.Verifying
                or LauncherUpdatePhase.ReadyToInstall or LauncherUpdatePhase.Applying;
            HasLauncherUpdateError = status.Phase == LauncherUpdatePhase.Error;
            LauncherUpdateProgress = status.TotalBytes > 0 ? status.Fraction * 100 : 0;
            LauncherUpdateSpeedText = status.SpeedBytesPerSec > 0
                ? PathHelper.FormatBytes(status.SpeedBytesPerSec) + "/s"
                : string.Empty;
            CanDownloadLauncherUpdate = status.Phase == LauncherUpdatePhase.UpdateAvailable
                && !IsLauncherUpdateBusy;
            CanRestartLauncherUpdate = status.Phase == LauncherUpdatePhase.ReadyToInstall
                && !IsLauncherUpdateBusy;

            LauncherUpdateStatusText = status.Phase switch
            {
                LauncherUpdatePhase.UpToDate => LocalizationManager.GetStringOrFallback(
                    "SettingsLauncherUpdateUpToDate", "You are up to date."),
                LauncherUpdatePhase.UpdateAvailable => string.Format(
                    LocalizationManager.GetStringOrFallback(
                        "SettingsLauncherUpdateAvailableFormat", "Version {0} is available."),
                    status.AvailableVersion ?? "вЂ”"),
                LauncherUpdatePhase.Downloading => string.Format(
                    LocalizationManager.GetStringOrFallback(
                        "SettingsLauncherUpdateDownloadingFormat", "Downloading updateвЂ¦ {0}%"),
                    Math.Round(status.Fraction * 100)),
                LauncherUpdatePhase.Verifying => LocalizationManager.GetStringOrFallback(
                    "SettingsLauncherUpdateVerifying", "Verifying the update packageвЂ¦"),
                LauncherUpdatePhase.ReadyToInstall => LocalizationManager.GetStringOrFallback(
                    "SettingsLauncherUpdateReady", "Ready to install. Restart Vellum to apply."),
                LauncherUpdatePhase.Applying => LocalizationManager.GetStringOrFallback(
                    "SettingsLauncherUpdateApplying", "Installing the update and restartingвЂ¦"),
                LauncherUpdatePhase.Error => status.ErrorMessage ?? LocalizationManager.GetStringOrFallback(
                    "SettingsLauncherUpdateError", "The launcher update failed."),
                _ => string.Empty
            };
        }

        public void ShowStatus(string message, bool isError)
        {
            _statusCts?.Cancel();
            _statusCts?.Dispose();
            _statusCts = new CancellationTokenSource();
            var token = _statusCts.Token;

            StatusMessage = message;
            IsStatusError = isError;
            IsStatusVisible = true;

            _ = DismissStatusAfterDelayAsync(token);
        }

        private async Task DismissStatusAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(4000, token);
                if (!token.IsCancellationRequested)
                    IsStatusVisible = false;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
