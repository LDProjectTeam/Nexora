using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Vellum.Tests;

/// <summary>
/// Redirects every test run to a throw-away AppData directory so the suite can never
/// overwrite the real launcher library, torrent session or artwork cache.
/// </summary>
internal static class TestDataIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VellumTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        Environment.SetEnvironmentVariable("VELLUM_DATA_DIR", directory);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        };
    }
}
