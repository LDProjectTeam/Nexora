using System;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    public class InstallerConsentTests
    {
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
