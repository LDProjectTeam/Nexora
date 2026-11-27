using System;
using System.Collections.Generic;
using System.Linq;

namespace Vellum.Core.Services
{
    /// <summary>
    /// The launcher is trusted only after it has read a valid one-time token from the
    /// inherited IPC pipe. Command-line markers and environment variables are
    /// intentionally ignored.
    /// </summary>
    public static class BootstrapperLaunchPolicy
    {
        public const string ChildMarkerArgument = "--bootstrapper-child";
        public const string LegacyMarkerArgument = "--bootstrapped";

        public static bool IsTrustedLaunch(string? ipcToken,
            IEnumerable<string>? arguments = null,
            string? environmentValue = null)
            => BootstrapperHandshake.IsValidToken(ipcToken);
    }
}
