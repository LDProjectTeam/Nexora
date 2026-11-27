using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vellum.Tests;

[CollectionDefinition("Resume installation boundary", DisableParallelization = true)]
public sealed class ResumeInstallationBoundaryCollection { }

[Collection("Resume installation boundary")]
public sealed class ResumeInstallationBoundaryTests
{
    [Fact]
    public async Task Partial_preallocated_torrent_resumes_download_and_never_starts_installer_on_no_peers()
    {
        var root = NewTempDirectory();
        try
        {
            var download = new StubDownloadManager { Result = OperationResult.Fail("No peers") };
            var installer = new StubInstaller();
            var manager = CreateManager(download, installer);
            var release = TorrentRelease("partial-resume");
            var staging = PathHelper.GetStagingPath(root, release.Id);
            Directory.CreateDirectory(staging);
            // rqbit preallocates the whole file even while most pieces are missing.
            File.WriteAllBytes(Path.Combine(staging, "payload.bin"), new byte[(int)release.DownloadSizeBytes]);
            var installation = PausedTorrent(release, root, downloaded: 256, total: 1024, completedMarker: true);
            manager.TrackInstallation(installation);

            var result = await manager.ResumeAsync(release.Id);

            Assert.False(result.Success);
            Assert.Equal(1, download.Calls);
            Assert.Equal(0, installer.Calls);
            // Resume converts a failed swarm attempt back to a retryable paused card.
            Assert.Equal(InstallState.Paused, installation.State);
            Assert.False(installation.DownloadCompleted);
            Assert.Equal(256, installation.DownloadedBytes);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Resume_waits_in_queue_and_does_not_start_installer_until_slot_is_granted()
    {
        var root = NewTempDirectory();
        try
        {
            var download = new StubDownloadManager { BlockFirstCall = true };
            var installer = new StubInstaller();
            var settings = new StubSettings { Settings = { MaxConcurrentDownloads = 1 } };
            var manager = CreateManager(download, installer, settings);
            var firstRelease = TorrentRelease("queue-first");
            var secondRelease = TorrentRelease("queue-resume");
            var first = PausedTorrent(firstRelease, root, 0, 1024);
            var second = PausedTorrent(secondRelease, root, 128, 1024);
            manager.TrackInstallation(first);
            manager.TrackInstallation(second);

            var firstTask = manager.InstallReleaseAsync(firstRelease, root);
            await download.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondTask = manager.InstallReleaseAsync(secondRelease, root);

            await WaitUntilAsync(() =>
                second.IsQueued && second.State == InstallState.Downloading);
            Assert.Equal(InstallState.Downloading, second.State);
            Assert.Equal(1, download.Calls);
            Assert.Equal(0, installer.Calls);

            download.ReleaseFirstCall.TrySetResult();
            await Task.WhenAll(firstTask, secondTask);
            Assert.Equal(0, installer.Calls);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Successful_torrent_download_is_the_only_path_into_installing()
    {
        var root = NewTempDirectory();
        try
        {
            var download = new StubDownloadManager { Result = OperationResult.Ok() };
            var installer = new StubInstaller();
            var manager = CreateManager(download, installer);
            var release = TorrentRelease("complete-then-install");
            var staging = PathHelper.GetStagingPath(root, release.Id);
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, "payload.bin"), new byte[(int)release.DownloadSizeBytes]);
            var installation = PausedTorrent(release, root, 256, 1024);
            installer.InstallationToCheck = installation;
            download.OnDownload = (_, progress) =>
            {
                download.StateWasDownloadingInDownload = installation.State == InstallState.Downloading;
                progress?.Report(new DownloadProgress
                {
                    IsTorrent = true,
                    BytesDownloaded = 1024,
                    TotalBytes = 1024,
                    Fraction = 1,
                    Phase = "Complete"
                });
            };
            manager.TrackInstallation(installation);

            var result = await manager.InstallReleaseAsync(release, root);

            Assert.True(result.Success, $"Result={result.Success}; error={result.ErrorMessage ?? "<null>"}; state={installation.State}; downloadCalls={download.Calls}; installCalls={installer.Calls}");
            Assert.Equal(1, download.Calls);
            Assert.Equal(1, installer.Calls);
            Assert.Equal(InstallState.Installed, installation.State);
            Assert.True(download.StateWasDownloadingInDownload);
            Assert.Equal(InstallState.Installing, installer.StateWhenCalled);
            Assert.True(installer.DownloadCompletedWhenCalled);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Declining_the_installer_leaves_the_card_paused_without_an_error()
    {
        var root = NewTempDirectory();
        try
        {
            var download = new StubDownloadManager { Result = OperationResult.Ok() };
            var installer = new StubInstaller { Result = OperationResult.Cancelled("cancelled by user") };
            var manager = CreateManager(download, installer);
            var release = TorrentRelease("declined-installer");
            var staging = PathHelper.GetStagingPath(root, release.Id);
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, "payload.bin"), new byte[(int)release.DownloadSizeBytes]);
            var installation = PausedTorrent(release, root, 256, 1024);
            manager.TrackInstallation(installation);

            var result = await manager.ResumeAsync(release.Id);

            Assert.False(result.Success);
            Assert.True(result.CancelledByUser);
            Assert.Equal(InstallState.Paused, installation.State);
            Assert.Null(installation.LastErrorMessage);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Second_resume_while_download_is_active_is_rejected_without_starting_another_download()
    {
        var root = NewTempDirectory();
        try
        {
            var download = new StubDownloadManager { BlockFirstCall = true };
            var installer = new StubInstaller();
            var manager = CreateManager(download, installer);
            var release = TorrentRelease("duplicate-resume");
            var installation = PausedTorrent(release, root, 128, 1024);
            manager.TrackInstallation(installation);

            var firstTask = manager.InstallReleaseAsync(release, root);
            await download.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondResult = await manager.ResumeAsync(release.Id);

            Assert.False(secondResult.Success);
            Assert.Equal(1, download.Calls);
            Assert.Equal(0, installer.Calls);

            download.ReleaseFirstCall.TrySetResult();
            await firstTask;
        }
        finally { TryDelete(root); }
    }

    private static GameManager CreateManager(
        StubDownloadManager download,
        StubInstaller installer,
        StubSettings? settings = null)
    {
        settings ??= new StubSettings();
        return new GameManager(
            NullLogger<GameManager>.Instance,
            new StubCatalog(),
            download,
            installer,
            new StubUpdater(),
            new StubVerifier(),
            settings,
            new StubDiskSpace());
    }

    private static GameReleaseCandidate TorrentRelease(string id) => new()
    {
        Id = id,
        CleanTitle = id,
        Version = "1.0",
        VersionType = GameVersionType.Repack,
        MagnetLink = "magnet:?xt=urn:btih:0123456789012345678901234567890123456789",
        DownloadSizeBytes = 1024,
        EstimatedInstalledSizeBytes = 2048
    };

    private static GameInstallation PausedTorrent(
        GameReleaseCandidate release,
        string root,
        long downloaded,
        long total,
        bool completedMarker = false) => new()
    {
        GameId = release.Id,
        DisplayName = release.CleanTitle,
        InstallPath = Path.Combine(root, release.CleanTitle),
        TargetLibraryPath = root,
        PendingRelease = release,
        State = InstallState.Paused,
        DownloadWasTorrent = true,
        DownloadedBytes = downloaded,
        DownloadTotalBytes = total,
        DownloadCompleted = completedMarker
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "Condition was not reached before timeout.");
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "VellumResumeBoundary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed class StubCatalog : IGameCatalog
    {
        public Task<IReadOnlyList<GameDefinition>> GetAllGamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GameDefinition>>([]);
        public Task<GameDefinition?> GetGameAsync(string gameId, CancellationToken ct = default) => Task.FromResult<GameDefinition?>(null);
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Settings { get; set; } = new() { MaxConcurrentDownloads = 2 };
        public bool IsFirstRun => false;
        public void Load() { }
        public Task LoadAsync() => Task.CompletedTask;
        public void Save() { }
        public Task SaveAsync() => Task.CompletedTask;
        public void Reset() { }
    }

    private sealed class StubDiskSpace : IDiskSpaceService
    {
        private static DiskSpaceRequirement Enough(string path, long size) => new()
        {
            TargetInstallPath = path,
            DriveRoot = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\",
            DownloadSizeBytes = size,
            RequiredFreeSpaceBytes = 0,
            AvailableFreeSpaceBytes = long.MaxValue / 2
        };
        public DiskSpaceRequirement CalculateRequirement(GameReleaseCandidate candidate, string targetInstallPath) => Enough(targetInstallPath, candidate.DownloadSizeBytes);
        public DiskSpaceRequirement CalculateRequirement(GameDefinition definition, string targetInstallPath) => Enough(targetInstallPath, definition.DownloadSizeBytes);
        public long GetAvailableFreeSpace(string targetPath, out string driveRoot) { driveRoot = "C:\\"; return long.MaxValue / 2; }
    }

    private sealed class StubDownloadManager : IDownloadManager
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public OperationResult Result { get; set; } = OperationResult.Fail("No peers");
        public bool BlockFirstCall { get; set; }
        public bool StateWasDownloadingInDownload { get; set; }
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<GameDefinition, IProgress<DownloadProgress>?>? OnDownload { get; set; }

        public async Task<OperationResult> DownloadAsync(GameDefinition game, string destinationFolder, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            OnDownload?.Invoke(game, progress);
            if (call == 1 && BlockFirstCall)
            {
                FirstCallStarted.TrySetResult();
                await ReleaseFirstCall.Task.WaitAsync(ct);
            }
            return Result;
        }

        public Task PauseAsync(string gameId) => Task.CompletedTask;
        public Task ResumeAsync(string gameId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAndSaveAsync(string gameId) => Task.CompletedTask;
        public Task StopSeedingAsync(string gameId) => Task.CompletedTask;
        public Task CancelAsync(string gameId) => Task.CompletedTask;
    }

    private sealed class StubInstaller : IInstaller
    {
        public int Calls { get; private set; }
        public OperationResult? Result { get; set; }
        public GameInstallation? InstallationToCheck { get; set; }
        public InstallState StateWhenCalled { get; private set; }
        public bool DownloadCompletedWhenCalled { get; private set; }
        public Task<OperationResult> InstallFromStagingAsync(string gameId, string stagingPath, string targetPath, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default, InstallOptions? options = null)
        {
            Calls++;
            StateWhenCalled = InstallationToCheck?.State ?? InstallState.NotInstalled;
            DownloadCompletedWhenCalled = InstallationToCheck?.DownloadCompleted == true;
            if (Result != null)
                return Task.FromResult(Result);
            Directory.CreateDirectory(targetPath);
            File.WriteAllText(Path.Combine(targetPath, "installed.marker"), "ok");
            return Task.FromResult(OperationResult.Ok(targetPath));
        }
        public Task<OperationResult> UninstallAsync(string gameId, string installPath) => Task.FromResult(OperationResult.Ok());
    }

    private sealed class StubUpdater : IUpdater
    {
        public Task<GameUpdate?> CheckForUpdateAsync(GameDefinition definition, GameInstallation installation, CancellationToken ct = default) => Task.FromResult<GameUpdate?>(null);
        public Task<OperationResult> ApplyUpdateAsync(GameDefinition definition, GameInstallation installation, GameUpdate update, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) => Task.FromResult(OperationResult.Fail("Not used"));
    }

    private sealed class StubVerifier : IFileVerifier
    {
        public Task<VerificationResult> VerifyAsync(GameDefinition definition, string installPath, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) => Task.FromResult(new VerificationResult { Passed = true });
        public Task<string> ComputeHashAsync(string filePath, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }
}
