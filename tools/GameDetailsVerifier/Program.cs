using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nexora.App.Services;
using Nexora.App.ViewModels;
using Nexora.App.Views;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Nexora.Core.Services;

namespace Nexora.GameDetailsVerifier
{
    internal static class Program
    {
        private static readonly string ShotRoot =
            Path.Combine(@"D:\launcher\.shots", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-game-details-v2");

        private static int _assertions;
        private static readonly string ArtworkDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexora", "artwork");

        [STAThread]
        private static int Main()
        {
            // Never touch the real launcher profile (library, downloads, caches).
            var dataDir = Path.Combine(Path.GetTempPath(), "nexora-verifier-data");
            Environment.SetEnvironmentVariable("NEXORA_DATA_DIR", dataDir);
            try { Directory.CreateDirectory(dataDir); } catch { }
            try
            {
                var steamCache = Path.Combine(dataDir, "steam-metadata");
                if (Directory.Exists(steamCache))
                    Directory.Delete(steamCache, recursive: true);
            }
            catch { }

            Console.WriteLine("==========================================================");
            Console.WriteLine(" NEXORA GAME DETAILS WINDOW — VERIFICATION RUNNER (v2)");
            Console.WriteLine("==========================================================");
            Console.WriteLine("Data dir: " + dataDir);
            Console.WriteLine("Screenshots: " + ShotRoot);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Nexora;component/Themes/DarkTheme.xaml", UriKind.Absolute)
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Nexora;component/Localization/Strings.ru.xaml", UriKind.Absolute)
            });
            app.Resources["NexoraAppIcon"] =
                new BitmapImage(new Uri("pack://application:,,,/Nexora;component/Assets/NEXORA_icon_light.ico"));
            LocalizationManager.SetLanguage("ru");

            Directory.CreateDirectory(ShotRoot);

            try
            {
                RunSteamServiceTests();
                RunViewModelTests();
                RunWindowTests();

                Console.WriteLine();
                Console.WriteLine("==========================================================");
                Console.WriteLine($" ALL TESTS PASSED — {_assertions} assertions, 0 failures");
                Console.WriteLine("==========================================================");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("FAILED: " + ex);
                return 1;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Steam service: matching, ambiguity, year check, language, cache
        // ────────────────────────────────────────────────────────────────────
        private static void RunSteamServiceTests()
        {
            Console.WriteLine();
            Console.WriteLine("[STEAM] store matching, ambiguity, language fallback, cache");

            var handler = new SteamStubHandler();
            var service = new SteamMetadataService(new HttpClient(handler));

            // 1. Exact title + year -> verified metadata.
            var cyberpunk = RunBlocking(() => service.GetMetadataAsync(new SteamMetadataRequest
            {
                Title = "Cyberpunk 2077",
                RawTitle = "Cyberpunk 2077 (2020) PC | Repack от FitGirl",
                ReleaseYear = 2020,
                LanguageCode = "ru"
            }));
            AssertTrue(cyberpunk.Metadata != null, "Cyberpunk resolved");
            AssertEqual(1091500, cyberpunk.Metadata!.AppId, "Cyberpunk AppID");
            AssertEqual("CD PROJEKT RED", string.Join(", ", cyberpunk.Metadata.Developers), "Cyberpunk developer");
            AssertEqual("CD PROJEKT RED", string.Join(", ", cyberpunk.Metadata.Publishers), "Cyberpunk publisher");
            AssertEqual("10 Dec, 2020", cyberpunk.Metadata.ReleaseDateText, "Official Steam release date");
            AssertEqual(2020, cyberpunk.Metadata.ReleaseYear, "Steam release year");
            AssertTrue(cyberpunk.Metadata.Genres.Contains("RPG"), "Genres parsed");
            AssertTrue(cyberpunk.Metadata.Platforms.Contains("windows"), "Platforms parsed");
            AssertTrue(cyberpunk.Metadata.MinimumRequirements.Contains("Windows 10"), "Minimum requirements parsed");
            AssertTrue(cyberpunk.Metadata.RecommendedRequirements.Contains("Windows 11"), "Recommended requirements parsed");
            AssertTrue(cyberpunk.Metadata.StoreUrl.EndsWith("/1091500/", StringComparison.Ordinal), "Store URL built");
            AssertTrue(cyberpunk.Metadata.HasDescription, "Description present");
            Console.WriteLine("  ✓ verified match, official fields, store URL");

            // 2. Second call must come from cache, with no new network requests.
            var requestsBefore = handler.RequestCount;
            var cached = RunBlocking(() => service.GetMetadataAsync(new SteamMetadataRequest
            {
                Title = "Cyberpunk 2077",
                RawTitle = "Cyberpunk 2077 (2020) PC | Repack от FitGirl",
                ReleaseYear = 2020,
                LanguageCode = "ru"
            }));
            AssertTrue(cached.IsFromCache, "Second lookup served from cache");
            AssertEqual(requestsBefore, handler.RequestCount, "No duplicate network request");
            Console.WriteLine("  ✓ cache hit, no duplicate request");

            // 3. Remake / different release year -> must NOT be shown.
            var nfs = RunBlocking(() => service.GetMetadataAsync(new SteamMetadataRequest
            {
                Title = "Need for Speed: Most Wanted",
                RawTitle = "Need for Speed: Most Wanted (2005) PC | Repack",
                ReleaseYear = 2005,
                LanguageCode = "en"
            }));
            AssertTrue(nfs.Metadata == null, "NFS 2005 not mapped to the 2012/2020 store page");
            AssertTrue(nfs.IsAmbiguous, "NFS year mismatch reported as ambiguous");
            Console.WriteLine("  ✓ year mismatch rejected (Need for Speed: Most Wanted)");

            // 4. Two equally good store results -> ambiguous, no metadata.
            var doom = RunBlocking(() => service.GetMetadataAsync(new SteamMetadataRequest
            {
                Title = "Doom",
                ReleaseYear = 2016,
                LanguageCode = "en"
            }));
            AssertTrue(doom.Metadata == null, "Ambiguous Doom result rejected");
            AssertTrue(doom.IsAmbiguous, "Ambiguity flag set");
            Console.WriteLine("  ✓ ambiguous title rejected");

            // 5. Russian description missing -> English fallback, Russian metadata object.
            var fallback = RunBlocking(() => service.GetMetadataAsync(new SteamMetadataRequest
            {
                Title = "Cyberpunk 2077",
                ReleaseYear = 2020,
                LanguageCode = "uk",
                AppId = 1091500
            }));
            AssertTrue(fallback.Metadata != null, "Ukrainian lookup resolved");
            AssertEqual("ukrainian", fallback.Metadata!.LanguageCode, "Requested language kept");
            AssertTrue(fallback.Metadata.HasDescription, "Description filled from English fallback");
            Console.WriteLine("  ✓ language fallback to English");

            // 6. Unknown title -> not found, no exception.
            var unknown = RunBlocking(() => service.GetMetadataAsync(new SteamMetadataRequest
            {
                Title = "Zzz Definitely Not A Real Game 12345",
                LanguageCode = "en"
            }));
            AssertTrue(unknown.Metadata == null, "Unknown title has no metadata");
            AssertTrue(unknown.IsNotFound || unknown.IsAmbiguous, "Unknown title reported as not found");
            Console.WriteLine("  ✓ unknown title handled");
        }

        // ────────────────────────────────────────────────────────────────────
        // View model: build types, fields separation, Steam, states, tabs
        // ────────────────────────────────────────────────────────────────────
        private static void RunViewModelTests()
        {
            Console.WriteLine();
            Console.WriteLine("[VM] build type, release fields, Steam UI state, install states");

            var repack = new GameReleaseCandidate
            {
                Id = "cyberpunk-repack",
                CleanTitle = "Cyberpunk 2077",
                RawTitle = "Cyberpunk 2077 (2020) PC | Repack от FitGirl",
                Version = "2.13",
                VersionType = GameVersionType.Repack,
                ReleaseAuthor = "FitGirl",
                SourceName = "Rutor",
                SourceId = "rutor",
                DownloadSizeBytes = 34_359_738_368L,
                EstimatedInstalledSizeBytes = 68_719_476_736L,
                Seeders = 184,
                Leechers = 42,
                PublishDate = DateTime.Now.AddDays(-2),
                Language = "Русский / English",
                DetailsUrl = "https://rutor.info/torrent/123456"
            };
            var portable = new GameReleaseCandidate
            {
                Id = "cyberpunk-portable",
                CleanTitle = "Cyberpunk 2077",
                RawTitle = "Cyberpunk 2077 (2020) PC | Portable от InsaneRamZes",
                Version = "2.13",
                VersionType = GameVersionType.Portable,
                ReleaseAuthor = "InsaneRamZes",
                SourceName = "Rutor",
                SourceId = "rutor",
                DownloadSizeBytes = 34_359_738_368L,
                EstimatedInstalledSizeBytes = 68_719_476_736L,
                Seeders = 96,
                Leechers = 8,
                PublishDate = DateTime.Now.AddDays(-1),
                Language = "Русский / English"
            };

            var inst = new GameInstallation { GameId = repack.Id, State = InstallState.NotInstalled };
            var aggregator = new ScriptedSearchAggregator(new[] { repack, portable }, repack);
            var steam = new SteamMetadataService(new HttpClient(new SteamStubHandler()));

            var card = NewReleaseCard(repack, inst, aggregator,
                Artwork("1091500_cover_v3.jpg"), Artwork("1091500_hero_v8.jpg"));
            var vm = new GameDetailsViewModel(card, aggregator, @"C:\Games", steam);
            PumpUntil(() => vm.HasSteamMetadata || vm.ShowSteamUnavailable, 6000);

            AssertEqual("Repack + Portable", vm.BuildTypeDisplay, "Both build types combined");
            AssertEqual("Repack и Portable доступны", vm.BuildTypeSubtext, "Both build types subtext");
            AssertEqual("FitGirl", vm.ReleaseAuthorDisplay, "Repack author shown separately");
            AssertEqual("Rutor", vm.SourceDisplay, "Source shown separately");
            AssertTrue(vm.IsReleaseDerived, "Release-derived card detected");
            AssertEqual("CD PROJEKT RED", vm.DeveloperDisplay, "Developer comes from Steam, not repack author");
            AssertEqual("CD PROJEKT RED", vm.PublisherDisplay, "Publisher comes from Steam");
            AssertEqual("10 Dec, 2020", vm.ReleaseDateDisplay, "Steam release date wins");
            AssertTrue(vm.HasHeroMeta, "Hero meta built");
            AssertTrue(vm.HasHeroImage, "Hero artwork set");
            AssertTrue(vm.HasSource, "Source chip visible");
            AssertTrue(vm.HasSteamMetadata, "Steam metadata loaded");
            AssertTrue(vm.CanOpenSteam, "Steam link available");
            AssertTrue(vm.HasSteamRequirements, "Requirements shown");
            AssertTrue(vm.HasSteamPlatforms, "Platforms shown");
            AssertTrue(vm.FullDescription.Contains("open-world"), "Steam description used");
            AssertTrue(vm.Genres.Contains("RPG"), "Steam genres used");
            Console.WriteLine("  ✓ build type / author / developer / publisher separation");

            // Installed state and storage actions.
            var installDir = Path.Combine(Path.GetTempPath(), "nexora-verifier-install", "Cyberpunk 2077");
            Directory.CreateDirectory(installDir);
            var installed = new GameInstallation
            {
                GameId = repack.Id,
                State = InstallState.Installed,
                InstallPath = installDir,
                InstalledVersion = "2.13",
                InstalledSizeBytes = 70_000_000_000L,
                InstalledAt = DateTime.Now.AddDays(-30),
                LastUpdatedAt = DateTime.Now.AddHours(-5)
            };
            card.UpdateInstallation(installed);
            PumpUntil(() => vm.IsInstalled, 2000);

            AssertTrue(vm.IsInstalled, "Installed state propagated");
            AssertEqual("Играть", vm.PrimaryActionLabel, "Primary action becomes Play");
            AssertEqual(installDir, vm.InstallPathDisplay, "Install path shown");
            AssertEqual(Path.GetDirectoryName(installDir), vm.LibraryPathDisplay, "Library path shown");
            AssertTrue(vm.CanOpenFolder, "Open folder available for existing directory");
            AssertTrue(vm.CopyInstallPathCommand.CanExecute(null), "Copy path enabled");
            AssertTrue(vm.VerifyCommand == card.VerifyCommand, "Verify reuses card command");
            AssertTrue(vm.UpdateCommand == card.UpdateCommand, "Update reuses card command");
            AssertTrue(vm.InstallCommand == card.InstallCommand, "Install command preserved");
            Console.WriteLine("  ✓ installed state, storage paths, existing commands");

            // Tabs.
            AssertTrue(vm.IsOverviewTabSelected, "Overview selected by default");
            vm.SelectSpecsTabCommand.Execute(null);
            AssertTrue(vm.IsSpecsTabSelected && !vm.IsOverviewTabSelected, "Specs tab selected");
            vm.SelectStorageTabCommand.Execute(null);
            AssertTrue(vm.IsStorageTabSelected, "Storage tab selected");
            vm.SelectOverviewTabCommand.Execute(null);
            AssertTrue(vm.IsOverviewTabSelected, "Overview tab restored");

            // Copy actions never crash, always give feedback.
            vm.CopySummary();
            AssertTrue(vm.HasNotificationMessage, "Copy summary feedback");
            vm.CopyTitle();
            AssertTrue(vm.HasNotificationMessage, "Copy title feedback");
            Console.WriteLine("  ✓ tabs and copy actions");

            // Steam unavailable / ambiguous states.
            var unavailableCard = NewCard(new GameDefinition
            {
                Id = "no-steam",
                Name = "Zzz Definitely Not A Real Game 12345",
                Description = "Some release description | Источник: Rutor",
                Developer = "Rutor",
                Publisher = "Rutor",
                Version = "1.0",
                Categories = new List<string> { "Repack" }
            }, new GameInstallation { GameId = "no-steam" }, new ScriptedSearchAggregator(Array.Empty<GameReleaseCandidate>(), null));

            var unavailableVm = new GameDetailsViewModel(unavailableCard,
                new ScriptedSearchAggregator(Array.Empty<GameReleaseCandidate>(), null),
                @"C:\Games", new SteamMetadataService(new HttpClient(new SteamStubHandler())));

            PumpUntil(() => unavailableVm.ShowSteamUnavailable || unavailableVm.HasSteamMetadata, 6000);
            AssertTrue(unavailableVm.ShowSteamUnavailable, "Unavailable Steam panel shown");
            AssertEqual("Информация Steam недоступна", unavailableVm.SteamStatusText, "Unavailable status text");
            AssertFalse(unavailableVm.HasSteamMetadata, "No unverified metadata displayed");
            AssertFalse(unavailableVm.CanOpenSteam, "Steam link hidden");
            AssertFalse(unavailableVm.HasFullDescription, "No fake description for a release-only card");
            AssertEqual("—", unavailableVm.SourceDisplay, "Publisher is not shown as source");
            Console.WriteLine("  ✓ Steam unavailable fallback");

            // Build type can never be the repack author.
            var repackOnly = new GameReleaseCandidate
            {
                CleanTitle = "Doom", RawTitle = "Doom (2016) PC | Repack от DODI", Version = "1.0",
                VersionType = GameVersionType.Repack, ReleaseAuthor = "DODI", SourceName = "Rutor", SourceId = "rutor"
            };
            var repackDef = new GameDefinition
            {
                Id = "doom", Name = "Doom",
                Description = "Doom (2016) PC | Repack от DODI | Источник: Rutor",
                Developer = "DODI", Publisher = "Rutor", Version = "1.0",
                Categories = new List<string>()
            };
            var repackAggregator = new ScriptedSearchAggregator(new[] { repackOnly }, repackOnly);
            var repackVm = new GameDetailsViewModel(NewCard(repackDef, new GameInstallation { GameId = "doom" }, repackAggregator),
                repackAggregator, @"C:\Games", null);
            PumpUntil(() => !repackVm.IsCheckingVersions, 3000);
            AssertEqual("Repack", repackVm.BuildTypeDisplay, "Repack badge (not author)");
            AssertTrue(repackVm.HasBuildType, "Known build type flagged");
            AssertEqual("DODI", repackVm.ReleaseAuthorDisplay, "Author shown in its own field");
            AssertEqual("—", repackVm.DeveloperDisplay, "Repack author not used as developer");
            AssertEqual("2016", repackVm.ReleaseDateDisplay, "Year from raw release title");
            Console.WriteLine("  ✓ repack badge never shows the author");

            vm.Dispose();
            unavailableVm.Dispose();
            repackVm.Dispose();
        }

        // ────────────────────────────────────────────────────────────────────
        // Windows: render every state at real size + DPI
        // ────────────────────────────────────────────────────────────────────
        private static void RunWindowTests()
        {
            Console.WriteLine();
            Console.WriteLine("[UI] rendering window states");

            var steam = new SteamMetadataService(new HttpClient(new SteamStubHandler()));

            // 1–3. Full metadata, both build types, uninstalled.
            var repack = Release("cyberpunk", "Cyberpunk 2077", "Cyberpunk 2077 (2020) PC | Repack от FitGirl",
                "FitGirl", GameVersionType.Repack, "2.13");
            var portable = Release("cyberpunk-p", "Cyberpunk 2077", "Cyberpunk 2077 (2020) PC | Portable от InsaneRamZes",
                "InsaneRamZes", GameVersionType.Portable, "2.13");
            var inst = new GameInstallation { GameId = repack.Id, State = InstallState.NotInstalled };
            var aggregator = new ScriptedSearchAggregator(new[] { repack, portable }, repack);
            var card = NewReleaseCard(repack, inst, aggregator,
                Artwork("1091500_cover_v3.jpg"), Artwork("1091500_hero_v8.jpg"));

            var window = new GameDetailsWindow(card, aggregator, @"C:\Games", steam);
            window.Show();
            PumpUntil(() => window.ViewModel.HasSteamMetadata || window.ViewModel.ShowSteamUnavailable, 6000);
            PumpUntil(() => !window.ViewModel.IsCheckingVersions, 3000);
            Render(window, "01-overview-steam-both-builds.png");

            window.ViewModel.SelectSpecsTabCommand.Execute(null);
            Render(window, "02-specs.png");

            window.ViewModel.SelectStorageTabCommand.Execute(null);
            Render(window, "03-storage-not-installed.png");

            // 4. Installed state.
            var installDir = Path.Combine(Path.GetTempPath(), "nexora-verifier-install", "Cyberpunk 2077");
            Directory.CreateDirectory(installDir);
            card.UpdateInstallation(new GameInstallation
            {
                GameId = repack.Id,
                State = InstallState.Installed,
                InstallPath = installDir,
                InstalledVersion = "2.13",
                InstalledSizeBytes = 70_000_000_000L,
                InstalledAt = DateTime.Now.AddDays(-30),
                LastUpdatedAt = DateTime.Now.AddHours(-5)
            });
            PumpUntil(() => window.ViewModel.IsInstalled, 2000);
            window.ViewModel.SelectStorageTabCommand.Execute(null);
            Render(window, "04-storage-installed.png");
            window.ViewModel.SelectOverviewTabCommand.Execute(null);
            Render(window, "05-overview-installed.png");
            window.Close();

            // 6. Steam unavailable, no artwork, repack only.
            var nfsCandidate = Release("nfs", "Need for Speed: Most Wanted",
                "Need for Speed: Most Wanted (2005) PC | Repack от R.G. Механики", "R.G. Механики",
                GameVersionType.Repack, "1.3");
            var nfsAgg = new ScriptedSearchAggregator(new[] { nfsCandidate }, nfsCandidate);
            var nfsCard = NewReleaseCard(nfsCandidate, new GameInstallation { GameId = nfsCandidate.Id }, nfsAgg);
            var nfsWindow = new GameDetailsWindow(nfsCard, nfsAgg, @"C:\Games", steam);
            nfsWindow.Show();
            PumpUntil(() => nfsWindow.ViewModel.ShowSteamUnavailable || nfsWindow.ViewModel.HasSteamMetadata, 6000);
            Render(nfsWindow, "06-overview-steam-unavailable.png");
            nfsWindow.Close();

            // 7. Long title + huge description + no artwork.
            var longTitle = "The Elder Scrolls V: Skyrim Special Edition — Официальное издание с дополнениями Dragonborn, Dawnguard и Hearthfire";
            var longDescription = string.Join("\n\n", Enumerable.Repeat(
                "Древний свиток предрекает возвращение драконов. Разрушьте империю, станьте Драконорождённым и решите судьбу Скайрима в этом огромном мире, где каждая пещера хранит свою историю, а каждый выбор меняет расстановку сил.",
                14));
            var longDef = new GameDefinition
            {
                Id = "skyrim",
                Name = longTitle,
                Description = longDescription,
                Developer = "Bethesda Game Studios",
                Publisher = "Bethesda Softworks",
                Version = "1.6.1170",
                ReleaseDate = "2016-10-28",
                SizeBytes = 50_000_000_000L,
                DownloadSizeBytes = 22_000_000_000L,
                Categories = new List<string> { "RPG", "Открытый мир", "Фэнтези" }
            };
            var longAgg = new ScriptedSearchAggregator(Array.Empty<GameReleaseCandidate>(), null);
            var longCard = NewCard(longDef, new GameInstallation { GameId = longDef.Id }, longAgg);
            var longWindow = new GameDetailsWindow(longCard, longAgg, @"C:\Games", null);
            longWindow.Show();
            PumpUntil(() => !longWindow.ViewModel.IsCheckingVersions, 3000);
            Render(longWindow, "07-long-title-no-artwork.png");
            longWindow.Close();

            // 8. Portable only (real cached artwork).
            var portableCandidate = Release("witcher3", "The Witcher 3: Wild Hunt",
                "The Witcher 3: Wild Hunt (2015) PC | Portable от InsaneRamZes", "InsaneRamZes",
                GameVersionType.Portable, "4.04");
            portableCandidate.EstimatedInstalledSizeBytes = 50_000_000_000L;
            portableCandidate.DownloadSizeBytes = 26_000_000_000L;
            var portableAgg = new ScriptedSearchAggregator(new[] { portableCandidate }, portableCandidate);
            var portableCard = NewReleaseCard(portableCandidate, new GameInstallation { GameId = portableCandidate.Id },
                portableAgg, Artwork("292030_cover_v3.jpg"), Artwork("292030_library_hero_v1.jpg"));
            var portableWindow = new GameDetailsWindow(portableCard, portableAgg, @"C:\Games", steam);
            portableWindow.Show();
            PumpUntil(() => !portableWindow.ViewModel.IsCheckingVersions, 3000);
            Render(portableWindow, "08-portable-only.png");
            portableWindow.Close();

            // 9. Same UI at 150% DPI.
            var dpiCard = NewCard(longDef, new GameInstallation { GameId = longDef.Id }, longAgg);
            var dpiWindow = new GameDetailsWindow(dpiCard, longAgg, @"C:\Games", null);
            dpiWindow.Show();
            PumpUntil(() => !dpiWindow.ViewModel.IsCheckingVersions, 3000);
            Render(dpiWindow, "09-dpi-150.png", 1.5);
            dpiWindow.Close();

            Console.WriteLine($"  ✓ {Directory.GetFiles(ShotRoot, "*.png").Length} screenshots written");
        }

        // ────────────────────────────────────────────────────────────────────
        // Test doubles
        // ────────────────────────────────────────────────────────────────────
        private sealed class ScriptedSearchAggregator : IGameSearchAggregator
        {
            private readonly IReadOnlyList<GameReleaseCandidate> _candidates;
            private readonly GameReleaseCandidate? _selected;

            public ScriptedSearchAggregator(IReadOnlyList<GameReleaseCandidate> candidates, GameReleaseCandidate? selected)
            {
                _candidates = candidates;
                _selected = selected;
            }

            public IReadOnlyList<IGameSource> Sources => Array.Empty<IGameSource>();
            public void RegisterSource(IGameSource source) { }

            public Task<IReadOnlyList<GameReleaseCandidate>> SearchAllSourcesAsync(string query, CancellationToken ct = default)
                => Task.FromResult(_candidates);

            public Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default)
                => Task.FromResult(_candidates);

            public Task<VersionSelectionResult> FindAndSelectBestVersionAsync(string gameTitle, string targetPath, CancellationToken ct = default)
            {
                var result = new VersionSelectionResult
                {
                    SelectedCandidate = _selected,
                    AllCandidates = _candidates.ToList(),
                    StrategyExplanation = _selected == null ? string.Empty : "Scripted release selected for verification."
                };

                if (_selected != null)
                {
                    result.SpaceRequirement = new DiskSpaceRequirement
                    {
                        TargetInstallPath = targetPath,
                        DriveRoot = Path.GetPathRoot(targetPath) ?? @"C:\",
                        VersionType = _selected.VersionType,
                        DownloadSizeBytes = _selected.DownloadSizeBytes,
                        EstimatedInstalledSizeBytes = _selected.EstimatedInstalledSizeBytes,
                        RequiredFreeSpaceBytes = _selected.EstimatedInstalledSizeBytes + 1_073_741_824L,
                        AvailableFreeSpaceBytes = 214_748_364_800L,
                        SafetyBufferBytes = 1_073_741_824L
                    };
                }

                return Task.FromResult(result);
            }

            public VersionSelectionResult EvaluateCandidate(GameReleaseCandidate candidate, string targetPath)
                => new() { SelectedCandidate = candidate };
        }

        private sealed class MockGameManager : IGameManager
        {
            public event EventHandler<GameInstallation>? InstallationStateChanged;
            public IReadOnlyDictionary<string, GameInstallation> Installations => new Dictionary<string, GameInstallation>();

            public GameInstallation GetInstallation(string gameId) => new() { GameId = gameId, State = InstallState.NotInstalled };
            public void TrackInstallation(GameInstallation installation) { }
            public Task<OperationResult> InstallAsync(string gameId, string targetPath, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
                => Task.FromResult(OperationResult.Ok());
            public Task<OperationResult> InstallReleaseAsync(GameReleaseCandidate release, string targetPath, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
                => Task.FromResult(OperationResult.Ok());
            public Task PauseAsync(string gameId) => Task.CompletedTask;
            public Task<OperationResult> ResumeAsync(string gameId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
                => Task.FromResult(OperationResult.Ok());
            public Task<OperationResult> RemoveDownloadAsync(string gameId) => Task.FromResult(OperationResult.Ok());
            public Task<VerificationResult> VerifyAsync(string gameId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
                => Task.FromResult(new VerificationResult { Passed = true });
            public Task<OperationResult> UpdateAsync(string gameId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
                => Task.FromResult(OperationResult.Ok());
            public Task<OperationResult> UninstallAsync(string gameId) => Task.FromResult(OperationResult.Ok());
            public Task CheckUpdatesAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task SaveLibraryAsync() => Task.CompletedTask;
            public Task LoadLibraryAsync() => Task.CompletedTask;
            public Task ShutdownAndSaveAllDownloadsAsync() => Task.CompletedTask;
            public IReadOnlyList<string> GetPendingAutoResumeGameIds() => Array.Empty<string>();
            public void MoveDownloadInQueue(string gameId, int delta) { }
            public void PromoteDownloadToFront(string gameId) { }
            public void NotifyMaxConcurrentDownloadsChanged() { }
            public void DismissCompletedDownload(string gameId) { }
            public void ClearCompletedDownloads() { }
        }

        private sealed class MockGameLauncher : IGameLauncher
        {
            public bool IsRunning(string gameId) => false;
            public GameRunStatus GetStatus(string gameId) => new() { GameId = gameId, State = GameRunState.NotRunning };
            public Task<OperationResult> LaunchAsync(GameDefinition def, GameInstallation inst) => Task.FromResult(OperationResult.Ok());
            public event EventHandler<GameRunStatus>? GameLaunched;
            public event EventHandler<GameRunStatus>? GameExited;
        }

        private sealed class MockSettingsService : ISettingsService
        {
            public AppSettings Settings { get; set; } = new() { DefaultInstallPath = @"C:\Games" };
            public bool IsFirstRun => false;
            public void Load() { }
            public Task LoadAsync() => Task.CompletedTask;
            public void Save() { }
            public Task SaveAsync() => Task.CompletedTask;
            public void Reset() { }
        }

        private sealed class MockArtworkService : IGameArtworkService
        {
            public Task<GameArtwork?> FindArtworkAsync(string gameTitle, CancellationToken cancellationToken = default)
                => Task.FromResult<GameArtwork?>(null);
            public Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, CancellationToken cancellationToken = default)
                => Task.FromResult<GameArtwork?>(null);
            public Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, ArtworkPriority priority, CancellationToken cancellationToken = default)
                => Task.FromResult<GameArtwork?>(null);
            public Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, ArtworkPriority priority, bool forceRefresh, CancellationToken cancellationToken = default)
                => Task.FromResult<GameArtwork?>(null);
        }

        /// <summary>
        /// Minimal in-memory Steam Store: search + appdetails, with a few fixtures
        /// that reproduce real matching problems (remake year, ambiguous titles).
        /// </summary>
        private sealed class SteamStubHandler : HttpMessageHandler
        {
            private int _requestCount;
            public int RequestCount => Volatile.Read(ref _requestCount);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requestCount);
                var uri = request.RequestUri!;
                var query = ParseQuery(uri.Query);
                string json;

                if (uri.AbsolutePath.Contains("storesearch", StringComparison.OrdinalIgnoreCase))
                {
                    var term = query.TryGetValue("term", out var t) ? t : string.Empty;
                    json = term switch
                    {
                        var s when s.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase) => StoreSearch(
                            (1091500, "Cyberpunk 2077")),
                        var s when s.Contains("Need for Speed", StringComparison.OrdinalIgnoreCase) => StoreSearch(
                            (1262560, "Need for Speed™ Most Wanted")),
                        var s when s.Equals("Doom", StringComparison.OrdinalIgnoreCase) || s.Contains("Doom 2", StringComparison.OrdinalIgnoreCase) => StoreSearch(
                            (379720, "DOOM"), (2280, "DOOM")),
                        var s when s.Contains("Disco Elysium", StringComparison.OrdinalIgnoreCase) => StoreSearch(
                            (632470, "Disco Elysium")),
                        _ => "{\"total\":0,\"items\":[]}"
                    };
                }
                else
                {
                    var appId = ParseAppId(query);
                    var language = query.TryGetValue("l", out var l) ? l : "english";
                    json = appId switch
                    {
                        1091500 => AppDetails(1091500, "Cyberpunk 2077", "10 Dec, 2020",
                            language == "english"
                                ? "<p>Cyberpunk 2077 is an open-world action-adventure RPG set in Night City.</p><ul><li>Explore a vast open world</li><li>Shape your own story</li></ul>"
                                : string.Empty,
                            language == "english"
                                ? "Cyberpunk 2077 is an open-world action-adventure RPG."
                                : string.Empty),
                        1262560 => AppDetails(1262560, "Need for Speed™ Most Wanted", "Jun 18, 2020",
                            "<p>Race in an open world and outrun the cops.</p>",
                            "Race in an open world and outrun the cops."),
                        379720 or 2280 => AppDetails(appId, "DOOM", "May 12, 2016",
                            "<p>Rip and tear.</p>", "Rip and tear."),
                        632470 => AppDetails(632470, "Disco Elysium", "Oct 15, 2019",
                            "<p>A detective RPG.</p>", "A detective RPG."),
                        _ => "{\"" + appId + "\":{\"success\":false}}"
                    };
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            private static Dictionary<string, string> ParseQuery(string query)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = pair.IndexOf('=');
                    if (idx <= 0)
                        continue;
                    result[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..]);
                }
                return result;
            }

            private static int ParseAppId(Dictionary<string, string> query)
                => query.TryGetValue("appids", out var value)
                   && int.TryParse(value.Split(',')[0], out var appId)
                    ? appId
                    : 0;

            private static string StoreSearch(params (int Id, string Name)[] items)
            {
                var builder = new StringBuilder();
                builder.Append("{\"total\":").Append(items.Length).Append(",\"items\":[");
                for (var i = 0; i < items.Length; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append("{\"type\":\"app\",\"name\":\"").Append(Escape(items[i].Name))
                        .Append("\",\"id\":").Append(items[i].Id).Append('}');
                }
                builder.Append("]}");
                return builder.ToString();
            }

            private static string AppDetails(int appId, string name, string releaseDate, string detailed, string shortText)
            {
                var builder = new StringBuilder();
                builder.Append("{\"").Append(appId).Append("\":{\"success\":true,\"data\":{");
                builder.Append("\"type\":\"game\",");
                builder.Append("\"name\":\"").Append(Escape(name)).Append("\",");
                builder.Append("\"short_description\":\"").Append(Escape(shortText)).Append("\",");
                builder.Append("\"detailed_description\":\"").Append(Escape(detailed)).Append("\",");
                builder.Append("\"developers\":[\"CD PROJEKT RED\"],");
                builder.Append("\"publishers\":[\"CD PROJEKT RED\"],");
                builder.Append("\"genres\":[{\"id\":\"3\",\"description\":\"RPG\"},{\"id\":\"1\",\"description\":\"Action\"}],");
                builder.Append("\"platforms\":{\"windows\":true,\"mac\":false,\"linux\":false},");
                builder.Append("\"release_date\":{\"coming_soon\":false,\"date\":\"").Append(Escape(releaseDate)).Append("\"},");
                builder.Append("\"header_image\":\"https://cdn.example/header.jpg\",");
                builder.Append("\"background_raw\":\"https://cdn.example/background.jpg\",");
                builder.Append("\"supported_languages\":\"English<strong>*</strong>, Russian, Ukrainian<br><strong>*</strong>languages with full audio support\",");
                builder.Append("\"pc_requirements\":{\"minimum\":\"<strong>OS:</strong> Windows 10<br><strong>Memory:</strong> 8 GB RAM\",\"recommended\":\"<strong>OS:</strong> Windows 11<br><strong>Memory:</strong> 16 GB RAM\"},");
                builder.Append("\"metacritic\":{\"score\":86},");
                builder.Append("\"website\":\"https://example.com\"");
                builder.Append("}}}");
                return builder.ToString();
            }

            private static string Escape(string value)
                => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }

        // ────────────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────────────
        private static GameCardViewModel NewCard(GameDefinition def, GameInstallation inst, IGameSearchAggregator aggregator)
            => new(def, inst, new MockGameManager(), new MockGameLauncher(),
                new MockSettingsService(), aggregator, new MockArtworkService(), new NotificationService());

        private static GameCardViewModel NewReleaseCard(GameReleaseCandidate candidate, GameInstallation inst,
            IGameSearchAggregator aggregator, string? cover = null, string? banner = null)
        {
            var card = new GameCardViewModel(candidate, inst, new MockGameManager(), new MockGameLauncher(),
                new MockSettingsService(), aggregator, new MockArtworkService(), new NotificationService());

            if (!string.IsNullOrWhiteSpace(cover) || !string.IsNullOrWhiteSpace(banner))
                card.ApplySteamArtwork(cover ?? string.Empty, banner ?? string.Empty);

            return card;
        }

        private static GameReleaseCandidate Release(string id, string title, string raw, string author,
            GameVersionType type, string version) => new()
        {
            Id = id,
            CleanTitle = title,
            RawTitle = raw,
            Version = version,
            VersionType = type,
            ReleaseAuthor = author,
            SourceName = "Rutor",
            SourceId = "rutor",
            DownloadSizeBytes = 34_359_738_368L,
            EstimatedInstalledSizeBytes = 68_719_476_736L,
            Seeders = 184,
            Leechers = 42,
            PublishDate = DateTime.Now.AddDays(-2),
            Language = "Русский / English",
            DetailsUrl = "https://rutor.info/torrent/123456"
        };

        private static string Artwork(string fileName)
        {
            var path = Path.Combine(ArtworkDir, fileName);
            return File.Exists(path) ? path : string.Empty;
        }

        private static void Render(Window window, string fileName, double scale = 1.0)
        {
            // Let tab fade / hero fade animations settle before snapshotting.
            PumpFor(400);

            var content = (UIElement)window.Content;
            var width = window.Width > 0 ? window.Width : 1040;
            var height = window.Height > 0 ? window.Height : 780;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            PumpOnce();

            var dpi = 96 * scale;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(width * scale),
                (int)Math.Round(height * scale),
                dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var path = Path.Combine(ShotRoot, fileName);
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            Console.WriteLine($"  → {fileName} ({bitmap.PixelWidth}×{bitmap.PixelHeight})");
        }

        private static void PumpOnce()
            => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

        private static void PumpFor(int milliseconds)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < milliseconds)
                PumpFrame(20);
        }

        private static void PumpFrame(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        private static void PumpUntil(Func<bool> condition, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
                PumpFrame(25);
        }

        private static T RunBlocking<T>(Func<Task<T>> action)
            => Task.Run(action).GetAwaiter().GetResult();

        private static void AssertTrue(bool condition, string name)
        {
            _assertions++;
            if (!condition)
                throw new Exception($"Assertion failed (expected TRUE): {name}");
        }

        private static void AssertFalse(bool condition, string name) => AssertTrue(!condition, name);

        private static void AssertEqual<T>(T expected, T actual, string name)
        {
            _assertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"Assertion failed: {name}\n  expected: '{expected}'\n  actual:   '{actual}'");
        }
    }
}
