using System;
using System.IO;
using Vellum.Core.Helpers;

namespace Vellum.Bootstrapper
{
    internal static class BootstrapperLog
    {
        private static readonly object Sync = new();

        public static void Write(string message)
        {
            try
            {
                PathHelper.EnsureDirectory(PathHelper.LogsDir);
                var path = Path.Combine(PathHelper.LogsDir,
                    "bootstrapper-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
                lock (Sync)
                    File.AppendAllText(path,
                        $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never prevent the launcher from starting.
            }
        }
    }
}
