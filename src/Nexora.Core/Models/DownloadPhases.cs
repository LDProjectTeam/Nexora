using System;

namespace Nexora.Core.Models
{
    /// <summary>
    /// Stable, language-independent identifiers for download/install phases.
    /// <see cref="DownloadProgress.Phase"/> is shown to the user and is localized;
    /// <see cref="DownloadProgress.PhaseKey"/> must never be translated.
    /// UI code maps these keys to visual states, so switching the language can no
    /// longer change how a download is interpreted.
    /// </summary>
    public static class DownloadPhases
    {
        public const string Queued = "queued";
        public const string CheckingDiskSpace = "checking-disk";
        public const string Starting = "starting";
        public const string FetchingMetadata = "fetching-metadata";
        public const string SearchingPeers = "searching-peers";
        public const string Downloading = "downloading";
        public const string CopyingFiles = "copying-files";
        public const string CheckingIntegrity = "checking-integrity";
        public const string Verifying = "verifying";
        public const string Paused = "paused";
        public const string MountingIso = "mounting-iso";
        public const string Extracting = "extracting";
        public const string RunningInstaller = "running-installer";
        public const string WaitingInstaller = "waiting-installer";
        public const string Prerequisites = "prerequisites";
        public const string Installing = "installing";
        public const string MovingFiles = "moving-files";
        public const string Finalizing = "finalizing";
        public const string Complete = "complete";

        /// <summary>
        /// Best-effort mapping for progress reported without a <c>PhaseKey</c>
        /// (older call sites or third-party sources). The strings below are the
        /// internal English identifiers used by the Core services.
        /// </summary>
        public static string? FromLegacyText(string? phase)
        {
            if (string.IsNullOrWhiteSpace(phase))
                return null;

            var text = phase.Trim();

            if (text.Equals("Downloading", StringComparison.OrdinalIgnoreCase)) return Downloading;
            if (text.Equals("Copying files", StringComparison.OrdinalIgnoreCase)) return CopyingFiles;
            if (text.Equals("Verifying", StringComparison.OrdinalIgnoreCase)) return Verifying;
            if (text.Equals("Paused", StringComparison.OrdinalIgnoreCase)) return Paused;
            if (text.Equals("Complete", StringComparison.OrdinalIgnoreCase)) return Complete;
            if (text.Equals("Installing", StringComparison.OrdinalIgnoreCase)) return Installing;
            if (text.Equals("Moving files", StringComparison.OrdinalIgnoreCase)) return MovingFiles;
            if (text.Equals("Extraction complete", StringComparison.OrdinalIgnoreCase)) return Finalizing;
            if (text.Equals("Mounting image", StringComparison.OrdinalIgnoreCase)) return MountingIso;
            if (text.Equals("Running installer", StringComparison.OrdinalIgnoreCase)) return RunningInstaller;
            if (text.StartsWith("Waiting for installer", StringComparison.OrdinalIgnoreCase)) return WaitingInstaller;
            if (text.Equals("Installing prerequisites", StringComparison.OrdinalIgnoreCase)) return Prerequisites;
            if (text.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase)) return Extracting;
            if (text.StartsWith("Checking available disk space", StringComparison.OrdinalIgnoreCase)) return CheckingDiskSpace;

            return null;
        }
    }
}
