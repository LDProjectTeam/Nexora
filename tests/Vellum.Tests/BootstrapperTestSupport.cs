using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Tests
{
    internal sealed class FakeLauncherUpdateService : ILauncherUpdateService
    {
        public event EventHandler<LauncherUpdateStatus>? StatusChanged;

        public LauncherUpdateStatus Status { get; set; } = new() { Phase = LauncherUpdatePhase.UpToDate };
        public LauncherUpdateManifest? AvailableManifest { get; set; }
        public LauncherApplyRequest? ApplyRequest { get; set; }
        public Func<LauncherUpdateStatus> CheckResult { get; set; } =
            () => new LauncherUpdateStatus { Phase = LauncherUpdatePhase.UpToDate };
        public Func<OperationResult> DownloadResult { get; set; } = () => OperationResult.Ok();
        public List<string> CheckedVersions { get; } = new();

        public Task<LauncherUpdateStatus> CheckForUpdatesAsync(string currentVersion,
            bool autoDownload = true, CancellationToken ct = default)
        {
            CheckedVersions.Add(currentVersion);
            Status = CheckResult();
            StatusChanged?.Invoke(this, Status);
            return Task.FromResult(Status);
        }

        public Task<OperationResult> DownloadAsync(string currentVersion, CancellationToken ct = default)
            => Task.FromResult(DownloadResult());

        public Task<OperationResult> DownloadBootstrapperAsync(BootstrapperUpdateInfo info,
            string destinationPath, IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
            => Task.FromResult(OperationResult.Ok(destinationPath));

        public LauncherApplyRequest CreateApplyRequest(string installDir, string entryPoint,
            string backupDir, string resultFile, int waitProcessId, bool relaunch)
        {
            if (ApplyRequest == null)
                throw new InvalidOperationException("No apply request configured.");
            ApplyRequest.InstallDir = installDir;
            ApplyRequest.BackupDir = backupDir;
            ApplyRequest.ResultFile = resultFile;
            ApplyRequest.WaitProcessId = waitProcessId;
            ApplyRequest.Relaunch = relaunch;
            return ApplyRequest;
        }

        public Task<OperationResult> PrepareRestartAsync(int currentProcessId, string currentVersion,
            CancellationToken ct = default) => Task.FromResult(OperationResult.Ok());

        public Task<LauncherUpdateResultRecord?> ConsumeBootstrapperResultAsync(CancellationToken ct = default)
            => Task.FromResult<LauncherUpdateResultRecord?>(null);
    }

    internal sealed class FakeLauncherIntegrityService : ILauncherIntegrityService
    {
        public Queue<LauncherIntegrityResult> Results { get; } = new();
        public int Calls { get; private set; }

        public Task<LauncherIntegrityResult> VerifyAsync(string appDirectory,
            string launcherExecutableName, CancellationToken ct = default)
        {
            Calls++;
            var result = Results.Count > 0
                ? Results.Dequeue()
                : new LauncherIntegrityResult { Passed = true, Version = "1.0.0" };
            return Task.FromResult(result);
        }

        public Task<LauncherInstalledState?> ReadInstalledStateAsync(CancellationToken ct = default)
            => Task.FromResult<LauncherInstalledState?>(null);
    }

    internal sealed class FakeProcessStarter : IProcessStarter
    {
        public List<(string File, IReadOnlyList<string> Args, string Directory, string? Input)> Calls { get; } = new();
        public Func<int, bool> ShouldStart { get; set; } = _ => true;
        public Action<string, IReadOnlyList<string>>? OnStart { get; set; }

        public ProcessStartResult Start(string fileName, IReadOnlyList<string> arguments,
            string workingDirectory, string? standardInput = null)
        {
            var call = Calls.Count;
            Calls.Add((fileName, arguments.ToList(), workingDirectory, standardInput));
            OnStart?.Invoke(fileName, arguments);
            return ShouldStart(call)
                ? new ProcessStartResult { Started = true, ProcessId = 999 }
                : new ProcessStartResult { Started = false, ErrorMessage = "test failure" };
        }
    }
}
