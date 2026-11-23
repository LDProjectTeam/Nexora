using System;
using System.Linq;

namespace Vellum.Core.Models
{
    public enum InstallerMode
    {
        /// <summary>Detect the installer type (Inno Setup / NSIS) and run it silently if known,
        /// otherwise open its window.</summary>
        Auto,

        /// <summary>Always open the installer window (for custom setup.exe).</summary>
        Interactive
    }

    /// <summary>Extra hints for the installer, usually taken from the catalog entry.</summary>
    public class InstallOptions
    {
        public InstallerMode Mode { get; set; } = InstallerMode.Auto;

        public bool IsRepack { get; set; }

        /// <summary>Custom command-line arguments for setup.exe. Use {DIR} for the target folder.
        /// Example: /S /D={DIR}</summary>
        public string? InstallerArgs { get; set; }

        public static InstallOptions FromDefinition(GameDefinition definition)
        {
            var source = definition.DownloadSources
                .OrderBy(s => s.Priority)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.InstallerArgs)
                                     || !string.IsNullOrWhiteSpace(s.InstallerMode));

            if (source == null)
                return new InstallOptions();

            return new InstallOptions
            {
                InstallerArgs = source.InstallerArgs,
                Mode = string.Equals(source.InstallerMode, "interactive", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(source.InstallerMode, "repack", StringComparison.OrdinalIgnoreCase)
                    ? InstallerMode.Interactive
                    : InstallerMode.Auto,
                IsRepack = string.Equals(source.InstallerMode, "repack", StringComparison.OrdinalIgnoreCase)
            };
        }
    }
}
