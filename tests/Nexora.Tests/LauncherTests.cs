using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Nexora.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Nexora.Tests
{
    public class LauncherTests
    {
        private readonly string _testCatalogPath;
        private readonly SettingsService _settings;
        private readonly GameCatalogService _catalog;
        private readonly RqbitDownloader _torrent;
        private readonly HttpDownloader _http;
        private readonly Installer _installer;
        private readonly FileVerifier _verifier;
        private readonly Updater _updater;
        private readonly DownloadManager _downloadManager;
        private readonly GameLauncher _gameLauncher;
        private readonly GameManager _gameManager;

        public LauncherTests()
        {
            _testCatalogPath = Path.Combine(
                Path.GetDirectoryName(PathHelper.BuiltInCatalogPath)!,
                "test_game",
                "games_test.json");

            _settings = new SettingsService(
                NullLogger<SettingsService>.Instance);
            _settings.Settings.AdditionalCatalogUrls.Add(_testCatalogPath);

            var httpClient = new HttpClient();

            _catalog = new GameCatalogService(
                NullLogger<GameCatalogService>.Instance,
                _settings,
                httpClient);

            _torrent = new RqbitDownloader(
                NullLogger<RqbitDownloader>.Instance,
                httpClient);

            _http = new HttpDownloader(
                httpClient,
                NullLogger<HttpDownloader>.Instance);

            _installer = new Installer(
                NullLogger<Installer>.Instance);

            _verifier = new FileVerifier(
                NullLogger<FileVerifier>.Instance);

            _downloadManager = new DownloadManager(
                NullLogger<DownloadManager>.Instance,
                _torrent,
                _http,
                _settings);

            _updater = new Updater(
                NullLogger<Updater>.Instance,
                _downloadManager,
                _installer,
                _verifier);

            _gameLauncher = new GameLauncher(
                NullLogger<GameLauncher>.Instance);

            _gameManager = new GameManager(
                NullLogger<GameManager>.Instance,
                _catalog,
                _downloadManager,
                _installer,
                _updater,
                _verifier,
                _settings,
                new DiskSpaceService(
                    NullLogger<DiskSpaceService>.Instance,
                    _settings));
        }

        [Fact]
        public async Task Test_1_Catalog_Loads_Games_Successfully()
        {
            await _catalog.RefreshAsync();
            var games = await _catalog.GetAllGamesAsync();

            Assert.NotEmpty(games);
            var testGame = await _catalog.GetGameAsync("cyber-runner-2088");
            Assert.NotNull(testGame);
            Assert.Equal("Cyber Runner 2088", testGame.Name);
            Assert.Equal("DemoGame.exe", testGame.Executable);
            Assert.NotEmpty(testGame.DownloadSources);
        }

        [Fact]
        public async Task Test_2_Full_Lifecycle_Install_Verify_Launch_Update_Uninstall()
        {
            var testInstallDir = Path.Combine(Path.GetTempPath(), "NexoraTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testInstallDir);

            try
            {
                // 1. Установка игры
                var installResult = await _gameManager.InstallAsync("cyber-runner-2088", testInstallDir);
                Assert.True(installResult.Success, $"Installation failed: {installResult.ErrorMessage}");

                var installation = _gameManager.GetInstallation("cyber-runner-2088");
                Assert.Equal(InstallState.Installed, installation.State);
                Assert.True(Directory.Exists(installation.InstallPath));
                Assert.True(File.Exists(Path.Combine(installation.InstallPath, "DemoGame.exe")));

                // 2. Проверка целостности файлов (SHA-256)
                var verifyResult = await _gameManager.VerifyAsync("cyber-runner-2088");
                Assert.True(verifyResult.Passed, $"Verification failed: {verifyResult.Summary}");

                // 3. Запуск игры и проверка отслеживания процесса
                var definition = await _catalog.GetGameAsync("cyber-runner-2088");
                Assert.NotNull(definition);

                var launchResult = await _gameLauncher.LaunchAsync(definition, installation);
                Assert.True(launchResult.Success, $"Launch failed: {launchResult.ErrorMessage}");
                Assert.True(_gameLauncher.IsRunning("cyber-runner-2088"), "Game should be reported as running");

                var secondLaunchResult = await _gameLauncher.LaunchAsync(
                    new GameDefinition { Id = "another-game", Name = "Another Game" }, installation);
                Assert.False(secondLaunchResult.Success,
                    "A second game should be blocked while the first one is running by default.");

                // Дадим поработать процессу и завершим
                var status = _gameLauncher.GetStatus("cyber-runner-2088");
                Assert.Equal(GameRunState.Running, status.State);

                // Завершим запущенный процесс теста
                var procs = System.Diagnostics.Process.GetProcessesByName("DemoGame");
                foreach (var p in procs)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }

                // 4. Обновление игры (с 1.0.0 на 1.1.0)
                await _gameManager.CheckUpdatesAsync();
                Assert.True(installation.UpdateAvailable, "Update should be detected for 1.0.0 -> 1.1.0");

                var updateResult = await _gameManager.UpdateAsync("cyber-runner-2088");
                Assert.True(updateResult.Success, $"Update failed: {updateResult.ErrorMessage}");
                Assert.Equal("1.1.0", installation.InstalledVersion);
                Assert.True(File.Exists(Path.Combine(installation.InstallPath, "patch_notes.txt")), "Updated file patch_notes.txt should exist");

                // 5. Удаление игры
                var uninstallResult = await _gameManager.UninstallAsync("cyber-runner-2088");
                Assert.True(uninstallResult.Success, $"Uninstall failed: {uninstallResult.ErrorMessage}");
                Assert.False(Directory.Exists(installation.InstallPath), "Install folder should be deleted after uninstall");
            }
            finally
            {
                if (Directory.Exists(testInstallDir))
                {
                    try { Directory.Delete(testInstallDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task Test_3_Download_State_Saved_And_Auto_Resumed_On_Startup()
        {
            var release = new GameReleaseCandidate
            {
                Id = "test-resume-game",
                CleanTitle = "Test Resume Game",
                Version = "1.0",
                TorrentFileUrl = "https://example.com/test.torrent",
                VersionType = GameVersionType.Portable
            };

            var installation = _gameManager.GetInstallation("test-resume-game");
            installation.DisplayName = release.CleanTitle;
            installation.PendingRelease = release;
            installation.TargetLibraryPath = "D:\\TestLibrary";
            installation.InstallPath = "D:\\TestLibrary\\TestResumeGame";
            installation.State = InstallState.Downloading;
            installation.WasDownloadingWhenClosed = true;
            _gameManager.TrackInstallation(installation);

            await _gameManager.SaveLibraryAsync();

            // Re-load library as if launcher was just started
            await _gameManager.LoadLibraryAsync();

            var pendingIds = _gameManager.GetPendingAutoResumeGameIds();
            Assert.Contains("test-resume-game", pendingIds);

            var loaded = _gameManager.GetInstallation("test-resume-game");
            Assert.Equal(InstallState.Paused, loaded.State);
            Assert.True(loaded.WasDownloadingWhenClosed);
            Assert.NotNull(loaded.PendingRelease);
            Assert.Equal("Test Resume Game", loaded.PendingRelease.CleanTitle);

            // Clean up
            await _gameManager.RemoveDownloadAsync("test-resume-game");
        }

        [Fact]
        public async Task Test_4_Download_Queue_Reordering_And_Completed_State_Management()
        {
            var g1 = _gameManager.GetInstallation("queue-game-1");
            g1.DisplayName = "Queue Game 1";
            g1.State = InstallState.Paused;
            g1.QueueOrder = 1;
            _gameManager.TrackInstallation(g1);

            var g2 = _gameManager.GetInstallation("queue-game-2");
            g2.DisplayName = "Queue Game 2";
            g2.State = InstallState.Paused;
            g2.QueueOrder = 2;
            _gameManager.TrackInstallation(g2);

            var g3 = _gameManager.GetInstallation("queue-game-3");
            g3.DisplayName = "Queue Game 3";
            g3.State = InstallState.Installed;
            g3.CompletedInSession = true;
            _gameManager.TrackInstallation(g3);

            // Move g2 up above g1
            _gameManager.MoveDownloadInQueue("queue-game-2", -1);
            Assert.True(g2.QueueOrder < g1.QueueOrder, "g2 should now precede g1 in queue order");

            // Promote g1 back to front
            _gameManager.PromoteDownloadToFront("queue-game-1");
            Assert.True(g1.QueueOrder < g2.QueueOrder, "g1 should be back at the front of the queue");

            // Dismiss completed download without uninstalling
            Assert.True(g3.CompletedInSession);
            _gameManager.DismissCompletedDownload("queue-game-3");
            Assert.False(g3.CompletedInSession);
            Assert.Equal(InstallState.Installed, g3.State);

            await _gameManager.RemoveDownloadAsync("queue-game-1");
            await _gameManager.RemoveDownloadAsync("queue-game-2");
            Assert.Equal(InstallState.NotInstalled, g1.State);
            Assert.Equal(InstallState.NotInstalled, g2.State);
        }

        [Fact]
        public async Task Test_5_Checksum_Mismatch_Aborts_Install_And_Removes_Payload()
        {
            var root = Path.Combine(Path.GetTempPath(), "NexoraChecksum_" + Guid.NewGuid().ToString("N"));
            var libraryDir = Path.Combine(root, "library");
            Directory.CreateDirectory(libraryDir);

            var gameId = "checksum-mismatch-game";
            var demoZip = Path.Combine(
                Path.GetDirectoryName(PathHelper.BuiltInCatalogPath)!, "test_game", "demo_game_v1.0.zip");
            var catalogPath = Path.Combine(root, "catalog.json");
            Assert.True(File.Exists(demoZip), $"Test fixture missing: {demoZip}");

            var catalog = new
            {
                catalogVersion = "1.0",
                games = new[]
                {
                    new
                    {
                        id = gameId,
                        name = "Checksum Mismatch Game",
                        version = "1.0.0",
                        installSubfolder = "ChecksumGame",
                        sizeBytes = 1024L,
                        downloadSizeBytes = 1024L,
                        downloadSources = new[]
                        {
                            new { type = "local", priority = 1, url = demoZip }
                        },
                        checksums = new
                        {
                            algorithm = "SHA256",
                            files = new Dictionary<string, string>
                            {
                                ["DemoGame.exe"] = new string('0', 64)
                            }
                        }
                    }
                }
            };

            await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(catalog));
            _settings.Settings.AdditionalCatalogUrls.Add(catalogPath);

            try
            {
                var result = await _gameManager.InstallAsync(gameId, libraryDir);

                Assert.False(result.Success, "Installation must fail when the checksums do not match.");

                var installation = _gameManager.GetInstallation(gameId);
                Assert.Equal(InstallState.Error, installation.State);
                Assert.False(string.IsNullOrWhiteSpace(installation.LastErrorMessage));

                // The archive must have been unpacked before the post-install checksum
                // check ran; otherwise this test would pass for the wrong reason.
                var installPath = Path.Combine(libraryDir, "ChecksumGame");
                Assert.True(
                    File.Exists(Path.Combine(installPath, "DemoGame.exe")),
                    "The archive should have been extracted before the checksum check failed.");

                var staging = PathHelper.GetStagingPath(libraryDir, gameId);
                Assert.False(Directory.Exists(staging),
                    "The corrupt payload must be removed so Retry downloads it again.");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
