using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Models;
using Nexora.Core.Services;

namespace Nexora.Tests;

public class InstallerTests
{
    [Fact]
    public async Task RepackAdoptsMatchingSiblingInstallFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-InstallerTest-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "Library", "temp", "plague-inc-evolved");
        var target = Path.Combine(root, "Plague Inc_Evolved");
        var actualInstall = Path.Combine(root, "Plague Inc Evolved");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(staging, "nested-release-folder"));
        Directory.CreateDirectory(Path.Combine(actualInstall, "Game"));
        File.WriteAllBytes(Path.Combine(actualInstall, "Game", "PlagueInc.exe"), Array.Empty<byte>());

        try
        {
            var installer = new Installer(NullLogger<Installer>.Instance);
            var result = await installer.InstallFromStagingAsync(
                "plague-inc-evolved",
                staging,
                target,
                options: new InstallOptions { IsRepack = true, Mode = InstallerMode.Interactive });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(actualInstall, result.InstallPath);
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepackWithoutSetupFailsInsteadOfBeingMarkedInstalled()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-InstallerTest-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "Library", "temp", "test-game");
        Directory.CreateDirectory(Path.Combine(staging, "nested-release-folder"));

        try
        {
            var installer = new Installer(NullLogger<Installer>.Instance);
            var result = await installer.InstallFromStagingAsync(
                "test-game",
                staging,
                Path.Combine(root, "installed"),
                options: new InstallOptions { IsRepack = true, Mode = InstallerMode.Interactive });

            Assert.False(result.Success);
            Assert.Contains("setup.exe", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
