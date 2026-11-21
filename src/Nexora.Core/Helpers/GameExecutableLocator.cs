using System;
using System.IO;
using System.Linq;

namespace Nexora.Core.Helpers
{
    public static class GameExecutableLocator
    {
        public static string? FindMainExecutable(string installPath)
        {
            if (!Directory.Exists(installPath))
                return null;

            try
            {
                return Directory.GetFiles(installPath, "*.exe", SearchOption.AllDirectories)
                    .Where(path => !IsInstallerOrUtility(Path.GetFileNameWithoutExtension(path)))
                    .OrderBy(path => string.Equals(Path.GetDirectoryName(path), installPath,
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenByDescending(path => new FileInfo(path).Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static string? FindSetupExecutable(string installPath)
        {
            if (!Directory.Exists(installPath))
                return null;

            try
            {
                return Directory.GetFiles(installPath, "*.exe", SearchOption.AllDirectories)
                    .Where(path =>
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        return !name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                            && (name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("install", StringComparison.OrdinalIgnoreCase));
                    })
                    .OrderBy(path => string.Equals(Path.GetDirectoryName(path), installPath,
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenByDescending(path => new FileInfo(path).Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsInstallerOrUtility(string name)
            => name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                || name.Contains("install", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
                || name.Contains("redist", StringComparison.OrdinalIgnoreCase)
                || name.Contains("dxsetup", StringComparison.OrdinalIgnoreCase);
    }
}
