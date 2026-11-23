using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vellum.Tests
{
    public class SearchAndDiskSpaceTests
    {
        private readonly TitleNormalizer _normalizer;
        private readonly DiskSpaceService _diskSpaceService;
        private readonly SettingsService _settingsService;
        private readonly VersionSelector _versionSelector;

        public SearchAndDiskSpaceTests()
        {
            _normalizer = new TitleNormalizer();
            _settingsService = new SettingsService(NullLogger<SettingsService>.Instance);
            _diskSpaceService = new DiskSpaceService(NullLogger<DiskSpaceService>.Instance, _settingsService);
            _versionSelector = new VersionSelector(_diskSpaceService, _settingsService);
        }

        [Fact]
        public void StagingPath_UsesLibraryDriveAndRemovesEmptyTempFolder()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "Vellum-" + Guid.NewGuid().ToString("N"));
            var libraryPath = Path.Combine(testRoot, "Vellum Library");
            var stagingPath = PathHelper.GetStagingPath(libraryPath, "test-game");
            var driveRoot = Path.GetPathRoot(testRoot)!;

            Assert.Equal(Path.Combine(driveRoot, "Library"), PathHelper.GetLibraryRoot(driveRoot));
            Assert.Equal(Path.Combine(libraryPath, "temp", "test-game"), stagingPath);

            Directory.CreateDirectory(stagingPath);
            Directory.Delete(stagingPath);
            PathHelper.DeleteEmptyStagingRoot(stagingPath);

            Assert.False(Directory.Exists(Path.Combine(libraryPath, "temp")));
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }

        [Fact]
        public void TitleNormalizer_DetectsPortableAndCleansTitle()
        {
            var raw = "Cyber Runner 2088 [Portable] (2024) PC | RePack от InsaneRamZes";
            var info = _normalizer.Normalize(raw);

            Assert.Equal(GameVersionType.Portable, info.VersionType);
            Assert.Contains("Cyber Runner 2088", info.CleanTitle);
        }

        [Fact]
        public void TitleNormalizer_MatchesDifferentSourceTitles()
        {
            var rutorTitle = "The Witcher 3: Wild Hunt / Ведьмак 3: Дикая Охота [v 4.04] (2015) PC | GOG";
            var query = "The Witcher 3: Wild Hunt";

            var info = _normalizer.Normalize(rutorTitle);

            Assert.True(_normalizer.AreTitlesMatching(info.CleanTitle, query));
            Assert.True(_normalizer.AreTitlesMatching(info.AltTitle, "Ведьмак 3"));
        }

        [Fact]
        public void VersionSelector_PrioritizesPortableOverRepackAndInstaller()
        {
            var candidates = new List<GameReleaseCandidate>
            {
                new()
                {
                    CleanTitle = "Test Game",
                    SourceName = "Rutor",
                    SourceId = "rutor",
                    SourcePriority = 1,
                    VersionType = GameVersionType.Repack,
                    DownloadSizeBytes = 10L * 1024 * 1024 * 1024,
                    Seeders = 150
                },
                new()
                {
                    CleanTitle = "Test Game",
                    SourceName = "Rutor",
                    SourceId = "rutor",
                    SourcePriority = 1,
                    VersionType = GameVersionType.Portable,
                    DownloadSizeBytes = 25L * 1024 * 1024 * 1024,
                    Seeders = 20
                },
                new()
                {
                    CleanTitle = "Test Game",
                    SourceName = "Rutor",
                    SourceId = "rutor",
                    SourcePriority = 1,
                    VersionType = GameVersionType.Installer,
                    DownloadSizeBytes = 20L * 1024 * 1024 * 1024,
                    Seeders = 50
                }
            };

            var result = _versionSelector.SelectBestVersion(candidates, @"C:\Games");

            Assert.NotNull(result.SelectedCandidate);
            Assert.Equal(GameVersionType.Portable, result.SelectedCandidate.VersionType);
            Assert.Equal("Rutor", result.SelectedCandidate.SourceName);
        }

        [Fact]
        public void VersionSelector_FallsBackToInstallerOrRepackWhenNoPortable()
        {
            var candidates = new List<GameReleaseCandidate>
            {
                new()
                {
                    CleanTitle = "Test Game",
                    SourceName = "Rutor",
                    SourceId = "rutor",
                    SourcePriority = 1,
                    VersionType = GameVersionType.Repack,
                    DownloadSizeBytes = 10L * 1024 * 1024 * 1024,
                    Seeders = 100
                },
                new()
                {
                    CleanTitle = "Test Game",
                    SourceName = "Rutor",
                    SourceId = "rutor",
                    SourcePriority = 1,
                    VersionType = GameVersionType.Installer,
                    DownloadSizeBytes = 15L * 1024 * 1024 * 1024,
                    Seeders = 100
                }
            };

            var result = _versionSelector.SelectBestVersion(candidates, @"C:\Games");

            Assert.NotNull(result.SelectedCandidate);
            Assert.Equal(GameVersionType.Repack, result.SelectedCandidate.VersionType);
            Assert.Equal("Rutor", result.SelectedCandidate.SourceName);
        }

        [Fact]
        public void DiskSpaceService_CalculatesPortableRequirement()
        {
            var candidate = new GameReleaseCandidate
            {
                CleanTitle = "Portable Game",
                VersionType = GameVersionType.Portable,
                DownloadSizeBytes = 10L * 1024 * 1024 * 1024, // 10 GB
                EstimatedInstalledSizeBytes = 10L * 1024 * 1024 * 1024
            };

            var req = _diskSpaceService.CalculateRequirement(candidate, @"C:\Games");

            // For portable: download (10 GB) + 1 GB safety = 11 GB
            long expected = 10L * 1024 * 1024 * 1024  // download
                          + 1024L * 1024 * 1024;       // safety buffer
            Assert.Equal(expected, req.RequiredFreeSpaceBytes);
        }

        [Fact]
        public void DiskSpaceService_CalculatesRepackRequirementWithExtractionBuffer()
        {
            var candidate = new GameReleaseCandidate
            {
                CleanTitle = "Repack Game",
                VersionType = GameVersionType.Repack,
                DownloadSizeBytes = 10L * 1024 * 1024 * 1024, // 10 GB
                EstimatedInstalledSizeBytes = 20L * 1024 * 1024 * 1024 // 20 GB
            };

            var req = _diskSpaceService.CalculateRequirement(candidate, @"C:\Games");

            // Repack: installed size + 1 GB safety; downloaded archive is temporary.
            long expected = 20L * 1024 * 1024 * 1024 + 1024L * 1024 * 1024;
            Assert.Equal(expected, req.RequiredFreeSpaceBytes);
            Assert.Equal(candidate.EstimatedInstalledSizeBytes, req.EstimatedInstalledSizeBytes);
        }

        [Fact]
        public void DiskSpaceService_CalculatesInstallerRequirementFromInstalledSize()
        {
            var candidate = new GameReleaseCandidate
            {
                CleanTitle = "Installer Game",
                VersionType = GameVersionType.Installer,
                DownloadSizeBytes = 10L * 1024 * 1024 * 1024,          // 10 GB archive
                EstimatedInstalledSizeBytes = 15L * 1024 * 1024 * 1024  // 15 GB installed
            };

            var req = _diskSpaceService.CalculateRequirement(candidate, @"C:\Games");

            // The archive is consumed by setup, so the lasting footprint is the installed
            // size plus the buffer — never a second copy of the download.
            long expected = 15L * 1024 * 1024 * 1024 + 1024L * 1024 * 1024;
            Assert.Equal(expected, req.RequiredFreeSpaceBytes);
        }

        [Fact]
        public void DiskSpaceRequirement_PreallocatedPayloadAllowsResumeWithoutRequiringItAgain()
        {
            const long gib = 1024L * 1024 * 1024;
            var req = new DiskSpaceRequirement
            {
                DriveRoot = @"D:\",
                DownloadSizeBytes = 65 * gib,
                RequiredFreeSpaceBytes = 65 * gib + gib,
                AvailableFreeSpaceBytes = 3 * gib,
                SafetyBufferBytes = gib
            };

            // The torrent engine preallocated the full payload, so only the safety
            // buffer is still required — the remaining pieces overwrite allocated files.
            Assert.True(req.HasEnoughSpaceForDownload(65 * gib));

            // Without preallocation the remaining 36 GiB genuinely need free space.
            Assert.False(req.HasEnoughSpaceForDownload(29 * gib));
            Assert.Equal(37 * gib, req.CalculateRemainingDownloadBytes(29 * gib));
            Assert.Equal(gib, req.CalculateRemainingDownloadBytes(65 * gib));
        }

        [Fact]
        public void DiskSpaceRequirement_AccuratelyReportsMissingSpace()
        {
            var req = new DiskSpaceRequirement
            {
                DriveRoot = @"D:\",
                RequiredFreeSpaceBytes = 50L * 1024 * 1024 * 1024, // 50 GB required
                AvailableFreeSpaceBytes = 20L * 1024 * 1024 * 1024  // 20 GB available
            };

            Assert.False(req.HasEnoughSpace);
            Assert.Equal(30L * 1024 * 1024 * 1024, req.MissingSpaceBytes);
            Assert.Contains("Недостаточно места", req.SummaryText);
            Assert.Contains("30 GB", req.SummaryText);
        }

        [Fact]
        public async Task GameSearchAggregator_GetLatestGames_PrefersPortableOverMorePopularRepack()
        {
            var source = new StaticGameSource(new[]
            {
                new GameReleaseCandidate
                {
                    CleanTitle = "Example Game",
                    SourceId = "rutor",
                    SourceName = "Rutor",
                    VersionType = GameVersionType.Repack,
                    Leechers = 500
                },
                new GameReleaseCandidate
                {
                    CleanTitle = "Example Game",
                    SourceId = "rutor",
                    SourceName = "Rutor",
                    VersionType = GameVersionType.Portable,
                    Leechers = 1
                }
            });
            var aggregator = new GameSearchAggregator(new[] { source }, _normalizer, _versionSelector,
                NullLogger<GameSearchAggregator>.Instance);

            var latest = await aggregator.GetLatestGamesAsync();

            var selected = Assert.Single(latest);
            Assert.Equal(GameVersionType.Portable, selected.VersionType);
        }

        [Fact]
        public async Task GameSearchAggregator_GetLatestGames_ReturnsAggregatedReleases()
        {
            var mockCatalogSource = new BuiltInCatalogSource(new GameCatalogService(
                NullLogger<GameCatalogService>.Instance, _settingsService, new System.Net.Http.HttpClient()));

            var aggregator = new GameSearchAggregator(
                new List<IGameSource> { mockCatalogSource },
                _normalizer,
                _versionSelector,
                NullLogger<GameSearchAggregator>.Instance);

            var latest = await aggregator.GetLatestGamesAsync();
            Assert.NotNull(latest);
        }

        [Fact]
        public async Task RutorGameSource_Search_ReturnsReleasesForCallOfDuty()
        {
            var rutor = new RutorGameSource(new System.Net.Http.HttpClient(), NullLogger<RutorGameSource>.Instance, _normalizer);
            var results = await rutor.SearchAsync("call of duty");
            Assert.NotEmpty(results);
        }

        private sealed class StaticGameSource : IGameSource
        {
            private readonly IReadOnlyList<GameReleaseCandidate> _releases;

            public StaticGameSource(IReadOnlyList<GameReleaseCandidate> releases) => _releases = releases;
            public string SourceId => "rutor";
            public string DisplayName => "Rutor";
            public int Priority { get; set; } = 1;
            public bool IsEnabled { get; set; } = true;

            public Task<IReadOnlyList<GameReleaseCandidate>> SearchAsync(string query, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<GameReleaseCandidate>>(Array.Empty<GameReleaseCandidate>());

            public Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default)
                => Task.FromResult(_releases);
        }
    }
}
