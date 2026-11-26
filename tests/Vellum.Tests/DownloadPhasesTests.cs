using Vellum.Core.Models;
using Xunit;

namespace Vellum.Tests
{
    public class DownloadPhasesTests
    {
        [Theory]
        [InlineData("Downloading", DownloadPhases.Downloading)]
        [InlineData("Copying files", DownloadPhases.CopyingFiles)]
        [InlineData("Verifying", DownloadPhases.Verifying)]
        [InlineData("Complete", DownloadPhases.Complete)]
        [InlineData("Paused", DownloadPhases.Paused)]
        [InlineData("Installing", DownloadPhases.Installing)]
        [InlineData("Moving files", DownloadPhases.MovingFiles)]
        [InlineData("Extraction complete", DownloadPhases.Finalizing)]
        [InlineData("Mounting image", DownloadPhases.MountingIso)]
        [InlineData("Running installer", DownloadPhases.RunningInstaller)]
        [InlineData("Waiting for installer (as Administrator)", DownloadPhases.WaitingInstaller)]
        [InlineData("Installing prerequisites", DownloadPhases.Prerequisites)]
        [InlineData("Extracting", DownloadPhases.Extracting)]
        [InlineData("Extracting Data1.bin (2/4)", DownloadPhases.Extracting)]
        [InlineData("Checking available disk space", DownloadPhases.CheckingDiskSpace)]
        public void FromLegacyText_MapsInternalPhaseNames(string text, string expected)
        {
            Assert.Equal(expected, DownloadPhases.FromLegacyText(text));
        }

        [Fact]
        public void FromLegacyText_DoesNotGuessLocalizedText()
        {
            // UI code must prefer DownloadProgress.PhaseKey; guessing translated text
            // is exactly the bug this API replaces.
            Assert.Null(DownloadPhases.FromLegacyText("Загрузка…"));
            Assert.Null(DownloadPhases.FromLegacyText("Проверка целостности данных…"));
            Assert.Null(DownloadPhases.FromLegacyText("Пошук пірів…"));
            Assert.Null(DownloadPhases.FromLegacyText(null));
            Assert.Null(DownloadPhases.FromLegacyText("  "));
        }

        [Theory]
        [InlineData(DownloadPhases.Downloading)]
        [InlineData(DownloadPhases.CopyingFiles)]
        [InlineData(DownloadPhases.CheckingIntegrity)]
        [InlineData(DownloadPhases.Verifying)]
        [InlineData(DownloadPhases.Extracting)]
        [InlineData(DownloadPhases.MovingFiles)]
        public void ReportsDeterminateProgress_TrueForMeasurablePhases(string phaseKey)
        {
            Assert.True(DownloadPhases.ReportsDeterminateProgress(phaseKey));
        }

        [Theory]
        [InlineData(DownloadPhases.Queued)]
        [InlineData(DownloadPhases.Starting)]
        [InlineData(DownloadPhases.FetchingMetadata)]
        [InlineData(DownloadPhases.SearchingPeers)]
        [InlineData(DownloadPhases.MountingIso)]
        [InlineData(DownloadPhases.RunningInstaller)]
        [InlineData(DownloadPhases.WaitingInstaller)]
        [InlineData(DownloadPhases.Prerequisites)]
        [InlineData(DownloadPhases.Installing)]
        [InlineData(DownloadPhases.Paused)]
        [InlineData(DownloadPhases.Complete)]
        [InlineData(null)]
        public void ReportsDeterminateProgress_FalseForUnknownPhases(string? phaseKey)
        {
            Assert.False(DownloadPhases.ReportsDeterminateProgress(phaseKey));
        }
    }
}
