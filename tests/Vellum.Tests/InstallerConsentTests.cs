using System;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    public class InstallerConsentTests
    {
        [Fact]
        public async Task DecliningInstallerConsent_IsAPauseNotAnInstallError()
        {
            var root = Path.Combine(Path.GetTempPath(), "Vellum-ConsentTest-" + Guid.NewGuid().ToString("N"));
            var staging = Path.Combine(root, "Library", "temp", "test-game");
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, "Setup.exe"), Array.Empty<byte>());

            try
            {
                InstallerConsent.SetHandler(_ => false);

                var installer = new Installer(NullLogger<Installer>.Instance);
                var result = await installer.InstallFromStagingAsync(
                    "test-game",
                    staging,
                    Path.Combine(root, "installed"),
                    options: new InstallOptions { IsRepack = true, Mode = InstallerMode.Interactive });

                Assert.False(result.Success);
                Assert.True(result.CancelledByUser,
                    "Declining the consent prompt must be reported as a user cancellation.");
            }
            finally
            {
                InstallerConsent.SetHandler(null);
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Request_DefaultsToAllow_AndHonoursTheUiHandler()
        {
            var request = new InstallerRunRequest(
                "game-id", "Game", @"C:\Downloads\setup.exe", 1024, IsElevated: false, IsSilent: true, "setup");

            try
            {
                InstallerConsent.SetHandler(null);
                Assert.True(InstallerConsent.Request(request), "Headless runs must keep the old behaviour.");

                InstallerConsent.SetHandler(_ => false);
                Assert.False(InstallerConsent.Request(request));

                InstallerRunRequest? captured = null;
                InstallerConsent.SetHandler(r => { captured = r; return true; });
                Assert.True(InstallerConsent.Request(request));
                Assert.Same(request, captured);

                // A broken dialog must fail closed, never run an unconfirmed installer.
                InstallerConsent.SetHandler(_ => throw new InvalidOperationException("UI failed"));
                Assert.False(InstallerConsent.Request(request));
            }
            finally
            {
                InstallerConsent.SetHandler(null);
            }
        }
    }
}
