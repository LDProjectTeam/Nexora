using System;
using System.Collections.Generic;

namespace Vellum.Core.Models
{
    public enum BootstrapperStage
    {
        CheckingUpdates,
        NoUpdates,
        UpdatingBootstrapper,
        CheckingIntegrity,
        AllChecksPassed,
        DownloadingUpdate,
        InstallingUpdate,
        VerifyingFiles,
        StartingLauncher,
        UpdateFailed,
        Ready
    }

    public class BootstrapperProgress
    {
        public BootstrapperStage Stage { get; set; }
        public string Message { get; set; } = string.Empty;
        public double Fraction { get; set; }
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public long SpeedBytesPerSec { get; set; }
        public long SecondsRemaining { get; set; } = -1;
        public string? ErrorMessage { get; set; }
        public bool CanRetry { get; set; }
        public bool CanLaunchCurrent { get; set; }

        public BootstrapperProgress Clone() => (BootstrapperProgress)MemberwiseClone();
    }

    public class BootstrapperRunRequest
    {
        public string AppDirectory { get; set; } = string.Empty;
        public string LauncherExecutableName { get; set; } = "Vellum.exe";
        public string CurrentAppVersion { get; set; } = "0.0.0";
        public List<string> LauncherArguments { get; set; } = new();

        /// <summary>The main app is already running; activate it instead of updating.</summary>
        public bool LauncherIsRunning { get; set; }

        public bool SkipLauncherUpdate { get; set; }
        public string? CurrentAppVersionOverride { get; set; }

        /// <summary>Append the marker that tells the main app it was launched by the bootstrapper.</summary>
        public bool MarkBootstrappedLaunch { get; set; }
    }

    public class BootstrapperRunResult
    {
        public bool Success { get; set; }
        public bool Launched { get; set; }
        public bool AppliedUpdate { get; set; }
        public string? ErrorMessage { get; set; }
        public bool CanRetry { get; set; }
        public bool CanLaunchCurrent { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;

        public static BootstrapperRunResult Ok(bool launched) => new()
        {
            Success = true,
            Launched = launched,
            CanLaunchCurrent = true
        };

        public static BootstrapperRunResult Failed(string message, bool canRetry, bool canLaunchCurrent)
            => new()
            {
                Success = false,
                ErrorMessage = message,
                CanRetry = canRetry,
                CanLaunchCurrent = canLaunchCurrent
            };
    }

    public class BootstrapperSelfUpdateRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string TargetPath { get; set; } = string.Empty;
        public string NewExecutablePath { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public int WaitProcessId { get; set; }
        public string Version { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public List<string> OriginalArguments { get; set; } = new();
        public string LauncherExecutablePath { get; set; } = string.Empty;
        public string? HealthFile { get; set; }
    }

    public class BootstrapperSelfUpdateResult
    {
        public bool Success { get; set; }
        public bool RestoredBackup { get; set; }
        public bool Launched { get; set; }
        public string? ErrorMessage { get; set; }

        public static BootstrapperSelfUpdateResult Ok(bool launched) => new()
        {
            Success = true,
            Launched = launched
        };

        public static BootstrapperSelfUpdateResult Failed(string message, bool restored, bool launched = false)
            => new()
            {
                Success = false,
                ErrorMessage = message,
                RestoredBackup = restored,
                Launched = launched
            };
    }

    public class LauncherIntegrityResult
    {
        public bool Passed { get; set; }
        public List<string> Problems { get; set; } = new();
        public string Version { get; set; } = string.Empty;
        public LauncherInstalledState? InstalledState { get; set; }
    }
}
