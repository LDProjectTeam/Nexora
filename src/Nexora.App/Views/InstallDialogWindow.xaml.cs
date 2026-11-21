using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Nexora.Core.Services;
using Nexora.App.Services;

namespace Nexora.App.Views
{
    /// <summary>
    /// Pre-install dialog: searches for the best version, checks disk space,
    /// shows the user details, and only enables download when everything is OK.
    /// </summary>
    public partial class InstallDialogWindow : Window
    {
        private readonly IGameSearchAggregator _aggregator;
        private readonly string _gameName;
        private readonly string _installBasePath;
        private readonly GameReleaseCandidate? _preselectedRelease;

        private VersionSelectionResult? _result;
        private bool _isUpdatingReleaseChoices;

        /// <summary>Set to true when the user clicked "Start Download".</summary>
        public bool DownloadConfirmed { get; private set; }

        /// <summary>The chosen release candidate (valid only when DownloadConfirmed == true).</summary>
        public GameReleaseCandidate? SelectedRelease => _result?.SelectedCandidate;

        public InstallDialogWindow(
            IGameSearchAggregator aggregator,
            string gameName,
            string installBasePath,
            GameReleaseCandidate? preselectedRelease = null)
        {
            InitializeComponent();
            _aggregator = aggregator;
            _gameName = gameName;
            _installBasePath = installBasePath;
            _preselectedRelease = preselectedRelease;
        }

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            await SearchAsync();
            await Dispatcher.InvokeAsync(CenterOverOwner);
        }

        private void CenterOverOwner()
        {
            if (Owner == null || !Owner.IsVisible)
                return;

            UpdateLayout();
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
        }

        private async Task SearchAsync()
        {
            ShowLoading(true);

            try
            {
                // Always evaluate best version across all connected sources
                _result = await _aggregator.FindAndSelectBestVersionAsync(
                    _gameName, _installBasePath, CancellationToken.None);

                // If aggregator found no releases from search, but caller had a preselected release, use that!
                if (_result.SelectedCandidate == null
                    && _preselectedRelease != null
                    && GameReleaseMatcher.IsForGame(_preselectedRelease, _gameName))
                {
                    _result.SelectedCandidate = _preselectedRelease;
                    _result.StrategyExplanation = LocalizationManager.GetString("InstallSelectedReleaseFormat", _preselectedRelease.SourceName);
                    _result.AllCandidates.Add(_preselectedRelease);
                }
            }
            catch (Exception ex)
            {
                if (_preselectedRelease != null
                    && GameReleaseMatcher.IsForGame(_preselectedRelease, _gameName))
                {
                    _result = new VersionSelectionResult
                    {
                        SelectedCandidate = _preselectedRelease,
                        AllCandidates = { _preselectedRelease },
                        StrategyExplanation = LocalizationManager.GetString("InstallSelectedReleaseFormat", _preselectedRelease.SourceName)
                    };
                }
                else
                {
                    ShowLoading(false);
                    ShowError(LocalizationManager.GetString("InstallSearchErrorFormat", ex.Message));
                    return;
                }
            }

            ShowLoading(false);

            if (_result.SelectedCandidate == null)
            {
                var errorMsg = _result.Errors.Count > 0
                    ? string.Join(Environment.NewLine, _result.Errors.Select(LocalizeSourceError))
                    : LocalizationManager.GetString("InstallNoSuitableVersion");
                ShowError(errorMsg);
                return;
            }

            ConfigureReleaseChoices(_result);
            PopulateResult(_result);
        }

        private GameReleaseCandidate? _bestPortableCandidate;
        private GameReleaseCandidate? _bestRepackCandidate;
        private List<GameReleaseCandidate> _allAvailableOptions = new();

        private static string ExtractVersionFromTitle(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle)) return string.Empty;
            var match = System.Text.RegularExpressions.Regex.Match(
                rawTitle,
                @"(?:\[|\(|\b)(?:v|ver|версия|build)[\s\.:]*([0-9]+(?:\.[0-9a-zA-Z_\-]+)*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private void ConfigureReleaseChoices(VersionSelectionResult result)
        {
            var candidates = result.AllCandidates
                .Where(candidate => GameReleaseMatcher.IsForGame(candidate, _gameName))
                .GroupBy(ReleaseIdentity, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(HasDownloadSource)
                    .ThenByDescending(candidate => candidate.Seeders)
                    .ThenByDescending(candidate => candidate.PublishDate ?? DateTime.MinValue)
                    .First())
                .Where(HasDownloadSource)
                .ToList();

            if (_preselectedRelease != null
                && GameReleaseMatcher.IsForGame(_preselectedRelease, _gameName)
                && candidates.All(candidate => ReleaseIdentity(candidate) != ReleaseIdentity(_preselectedRelease)))
            {
                candidates.Insert(0, _preselectedRelease);
            }

            _allAvailableOptions = candidates;

            _bestPortableCandidate = candidates
                .Where(candidate => candidate.VersionType == GameVersionType.Portable)
                .OrderBy(candidate => candidate.SourcePriority)
                .ThenByDescending(candidate => candidate.Seeders)
                .ThenByDescending(candidate => candidate.PublishDate ?? DateTime.MinValue)
                .FirstOrDefault();

            _bestRepackCandidate = candidates
                .Where(candidate => candidate.VersionType == GameVersionType.Repack || candidate.VersionType == GameVersionType.Installer)
                .OrderBy(candidate => candidate.SourcePriority)
                .ThenByDescending(candidate => candidate.Seeders)
                .ThenByDescending(candidate => candidate.PublishDate ?? DateTime.MinValue)
                .FirstOrDefault();

            _isUpdatingReleaseChoices = true;

            // Decide which release should be selected initially:
            GameReleaseCandidate? initialSelection = null;
            if (_preselectedRelease != null)
            {
                // If user clicked a specific card, respect that card's release!
                initialSelection = candidates.FirstOrDefault(c => ReleaseIdentity(c) == ReleaseIdentity(_preselectedRelease))
                    ?? candidates.FirstOrDefault();
            }
            else
            {
                // Default: prefer portable if healthy, otherwise best repack/available
                initialSelection = _bestPortableCandidate ?? _bestRepackCandidate ?? candidates.FirstOrDefault();
            }

            // Configure Version Switcher Bar (when both Portable and Repack/Installer exist)
            if (_bestPortableCandidate != null && _bestRepackCandidate != null)
            {
                VersionSwitcherBar.Visibility = Visibility.Visible;
                SelectPortableTabBtn.Content = $"🟢 Portable ({PathHelper.FormatBytes(_bestPortableCandidate.DownloadSizeBytes)}, {LocalizationManager.GetString("SeedsCountFormat", _bestPortableCandidate.Seeders)})";
                SelectRepackTabBtn.Content = $"🗜 Repack ({PathHelper.FormatBytes(_bestRepackCandidate.DownloadSizeBytes)}, {LocalizationManager.GetString("SeedsCountFormat", _bestRepackCandidate.Seeders)})";
            }
            else
            {
                VersionSwitcherBar.Visibility = Visibility.Collapsed;
            }

            UpdateDropdownForCurrentVersion(initialSelection);

            // No portable warning if no portable exists
            if (_bestPortableCandidate == null)
            {
                NoPortablePanel.Visibility = Visibility.Visible;
                NoPortableWarningText.Text = result.Errors.Count > 0
                    ? LocalizationManager.GetString("InstallNoPortableErrors")
                    : candidates.Count > 1
                        ? LocalizationManager.GetString("InstallNoPortableMultiple")
                        : LocalizationManager.GetString("InstallNoPortableSingle");
            }
            else
            {
                NoPortablePanel.Visibility = Visibility.Collapsed;
            }

            if (initialSelection != null)
            {
                result.SelectedCandidate = initialSelection;
                result.SpaceRequirement = _aggregator.EvaluateCandidate(initialSelection, _installBasePath).SpaceRequirement;
            }

            _isUpdatingReleaseChoices = false;
            UpdateSwitcherTabStyles(initialSelection);
        }

        private void UpdateDropdownForCurrentVersion(GameReleaseCandidate? current)
        {
            if (current == null) return;

            // When user is on Repack tab, show other Repacks; when on Portable tab, show other Portables.
            // If there's only 1 version type or no tabs, show all candidates.
            bool hasBothTabs = VersionSwitcherBar.Visibility == Visibility.Visible;
            var filtered = hasBothTabs
                ? _allAvailableOptions.Where(c => (current.VersionType == GameVersionType.Portable) == (c.VersionType == GameVersionType.Portable)).ToList()
                : _allAvailableOptions;

            ReleaseChoiceBox.ItemsSource = filtered;
            ReleaseChoiceBox.SelectedItem = current;
            bool showDropdown = filtered.Count > 1;
            ReleaseChoiceBox.Visibility = showDropdown ? Visibility.Visible : Visibility.Collapsed;
            ReleaseChoiceLabel.Visibility = showDropdown ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateSwitcherTabStyles(GameReleaseCandidate? current)
        {
            if (current == null || VersionSwitcherBar.Visibility != Visibility.Visible)
                return;

            if (current.VersionType == GameVersionType.Portable)
            {
                SelectPortableTabBtn.Background = new SolidColorBrush(Color.FromRgb(46, 204, 113));
                SelectPortableTabBtn.Foreground = Brushes.White;
                SelectRepackTabBtn.Background = Brushes.Transparent;
                SelectRepackTabBtn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }
            else
            {
                SelectRepackTabBtn.Background = new SolidColorBrush(Color.FromRgb(230, 126, 34));
                SelectRepackTabBtn.Foreground = Brushes.White;
                SelectPortableTabBtn.Background = Brushes.Transparent;
                SelectPortableTabBtn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }
        }

        private void SelectPortableTabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_bestPortableCandidate != null)
            {
                SelectRelease(_bestPortableCandidate);
            }
        }

        private void SelectRepackTabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_bestRepackCandidate != null)
            {
                SelectRelease(_bestRepackCandidate);
            }
        }

        private void SelectRelease(GameReleaseCandidate candidate)
        {
            if (_result == null) return;
            _isUpdatingReleaseChoices = true;
            UpdateDropdownForCurrentVersion(candidate);
            _result.SelectedCandidate = candidate;
            _result.SpaceRequirement = _aggregator.EvaluateCandidate(candidate, _installBasePath).SpaceRequirement;
            _isUpdatingReleaseChoices = false;

            UpdateSwitcherTabStyles(candidate);
            PopulateResult(_result);
        }

        private static bool HasDownloadSource(GameReleaseCandidate candidate)
            => !string.IsNullOrWhiteSpace(candidate.MagnetLink)
                || !string.IsNullOrWhiteSpace(candidate.TorrentFileUrl)
                || !string.IsNullOrWhiteSpace(candidate.DirectUrl);

        private static string ReleaseIdentity(GameReleaseCandidate candidate)
            => $"{NormalizeReleasePart(candidate.RawTitle.Length > 0 ? candidate.RawTitle : candidate.CleanTitle)}|" +
               $"{candidate.VersionType}";

        private static string NormalizeReleasePart(string value)
            => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private void ReleaseChoiceBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isUpdatingReleaseChoices || _result == null ||
                ReleaseChoiceBox.SelectedItem is not GameReleaseCandidate candidate)
                return;

            _result.SelectedCandidate = candidate;
            _result.SpaceRequirement = _aggregator.EvaluateCandidate(candidate, _installBasePath).SpaceRequirement;
            UpdateSwitcherTabStyles(candidate);
            PopulateResult(_result);
        }

        private void PopulateResult(VersionSelectionResult result)
        {
            var candidate = result.SelectedCandidate!;
            var space = result.SpaceRequirement;

            // Game title
            GameTitleText.Text = candidate.CleanTitle;

            // Source badge
            SourceBadge.Text = GetLocalizedSourceName(candidate);

            // Version type badge
            var versionLabel = candidate.VersionType switch
            {
                GameVersionType.Portable  => "Portable",
                GameVersionType.Installer => LocalizationManager.GetString("InstallVersionInstaller"),
                GameVersionType.Archive   => LocalizationManager.GetString("InstallVersionArchive"),
                GameVersionType.Repack    => "🗜 Repack",
                _                        => LocalizationManager.GetString("InstallVersionUnknown")
            };
            VersionTypeText.Text = versionLabel;

            // Game version badge (e.g. "v 1.10.1")
            var gameVer = !string.IsNullOrWhiteSpace(candidate.Version)
                ? candidate.Version
                : ExtractVersionFromTitle(candidate.RawTitle);
            if (!string.IsNullOrWhiteSpace(gameVer))
            {
                GameVersionBorder.Visibility = Visibility.Visible;
                GameVersionText.Text = gameVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? gameVer : $"v {gameVer}";
            }
            else
            {
                GameVersionBorder.Visibility = Visibility.Collapsed;
            }

            InstallFlowText.Text = candidate.VersionType switch
            {
                GameVersionType.Portable =>
                    LocalizationManager.GetString("InstallFlowPortable"),
                GameVersionType.Repack =>
                    LocalizationManager.GetString("InstallFlowRepack"),
                _ =>
                    LocalizationManager.GetString("InstallFlowOther")
            };

            // Portable → green, everything else → orange
            VersionTypeBorder.Background = candidate.VersionType == GameVersionType.Portable
                ? new SolidColorBrush(Color.FromRgb(46, 204, 113))
                : new SolidColorBrush(Color.FromRgb(230, 126, 34));

            // Sizes
            DownloadSizeText.Text = candidate.DownloadSizeBytes > 0
                ? PathHelper.FormatBytes(candidate.DownloadSizeBytes)
                : LocalizationManager.GetString("InstallUnknown");

            InstalledSizeText.Text = space != null
                ? PathHelper.FormatBytes(space.EstimatedInstalledSizeBytes)
                : (candidate.EstimatedInstalledSizeBytes > 0
                    ? PathHelper.FormatBytes(candidate.EstimatedInstalledSizeBytes)
                    : "~");

            SeedersText.Text = candidate.Seeders >= 0
                ? candidate.Seeders.ToString()
                : LocalizationManager.GetString("InstallNotAvailable");

            SourceDetailText.Text = candidate.AltTitle.Length > 0
                ? candidate.AltTitle
                : candidate.CleanTitle;

            // Disk space block
            if (space != null)
            {
                AvailableSpaceText.Text = space.FormattedAvailable;
                RequiredSpaceText.Text  = space.FormattedRequired;

                if (!space.HasEnoughSpace)
                {
                    DiskSpaceBorder.Background = new SolidColorBrush(Color.FromRgb(58, 27, 27));
                    InsufficientPanel.Visibility = Visibility.Visible;
                    MissingSpaceText.Text =
                        LocalizationManager.GetString("InstallInsufficientSpaceFormat",
                            space.FormattedMissing, space.DriveRoot);
                    DownloadButton.IsEnabled = false;
                }
                else
                {
                    DiskSpaceBorder.Background = new SolidColorBrush(Color.FromRgb(27, 58, 45));
                    DownloadButton.IsEnabled = true;
                }
            }
            else
            {
                // No space info — allow but warn
                AvailableSpaceText.Text = LocalizationManager.GetString("InstallNotAvailable");
                RequiredSpaceText.Text  = LocalizationManager.GetString("InstallNotAvailable");
                DownloadButton.IsEnabled = true;
            }

            // Strategy explanation (collapsed by default, shown if interesting)
            if (!string.IsNullOrWhiteSpace(result.StrategyExplanation))
            {
                StrategyText.Text = LocalizationManager.GetString(
                    "InstallStrategySelectedFormat", GetLocalizedSourceName(candidate));
                StrategyPanel.Visibility = Visibility.Visible;
            }

            // Non-fatal source errors
            if (result.Errors.Count > 0)
            {
                SourceErrorsText.Text = LocalizationManager.GetString("InstallSomeSourcesUnavailable") +
                    Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Select(LocalizeSourceError));
                SourceErrorsPanel.Visibility = Visibility.Visible;
            }

            ResultPanel.Visibility = Visibility.Visible;
        }

        private void ShowLoading(bool visible)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                LoadingPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible)
                {
                    ResultPanel.Visibility = Visibility.Collapsed;
                    ErrorPanel.Visibility  = Visibility.Collapsed;
                }
            });
        }

        private static string GetLocalizedSourceName(GameReleaseCandidate candidate)
            => string.Equals(candidate.SourceId, "catalog", StringComparison.OrdinalIgnoreCase)
                ? LocalizationManager.GetString("BuiltInCatalogName")
                : candidate.SourceName;

        private static string LocalizeSourceError(string error)
        {
            const string sourcePrefix = "Источник '";
            const string unavailableMarker = "' недоступен: ";
            if (error.StartsWith(sourcePrefix, StringComparison.Ordinal))
            {
                var markerIndex = error.IndexOf(unavailableMarker, sourcePrefix.Length, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    var sourceName = error.Substring(sourcePrefix.Length, markerIndex - sourcePrefix.Length);
                    var detail = error[(markerIndex + unavailableMarker.Length)..];
                    if (sourceName is "Каталог Nexora" or "Nexora Catalog")
                        sourceName = LocalizationManager.GetString("BuiltInCatalogName");
                    return LocalizationManager.GetString("InstallSourceUnavailableFormat", sourceName, detail);
                }
            }

            if (error == "Нет активных источников для поиска.")
                return LocalizationManager.GetString("InstallNoActiveSources");

            return error;
        }

        private void ShowError(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ErrorText.Text = message;
                ErrorPanel.Visibility  = Visibility.Visible;
                ResultPanel.Visibility = Visibility.Collapsed;
                DownloadButton.IsEnabled = false;
            });
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not System.Windows.Controls.Button &&
                e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadButton.IsEnabled = false; // protect from a double click
            DownloadConfirmed = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadConfirmed = false;
            DialogResult = false;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadConfirmed = false;
            DialogResult = false;
            Close();
        }
    }
}
