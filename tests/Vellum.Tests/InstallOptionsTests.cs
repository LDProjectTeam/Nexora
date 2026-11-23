using Vellum.Core.Models;

namespace Vellum.Tests;

public class InstallOptionsTests
{
    [Fact]
    public void RepackUsesInteractiveInstallerMode()
    {
        var definition = new GameDefinition
        {
            DownloadSources =
            {
                new DownloadSource { Type = "torrent", InstallerMode = "repack" }
            }
        };

        var options = InstallOptions.FromDefinition(definition);

        Assert.True(options.IsRepack);
        Assert.Equal(InstallerMode.Interactive, options.Mode);
    }

    [Fact]
    public void InstallerWithoutExplicitModeStaysInAutomaticMode()
    {
        var definition = new GameDefinition
        {
            DownloadSources =
            {
                new DownloadSource { Type = "torrent" }
            }
        };

        var options = InstallOptions.FromDefinition(definition);

        Assert.False(options.IsRepack);
        Assert.Equal(InstallerMode.Auto, options.Mode);
    }
}
