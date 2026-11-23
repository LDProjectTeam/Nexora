using System;
using System.IO;
using Vellum.Core.Helpers;

namespace Vellum.Tests;

public class GameExecutableLocatorTests
{
    [Fact]
    public void FindMainExecutable_IgnoresSetupAndFindsGameExeRecursively()
    {
        var root = Path.Combine(Path.GetTempPath(), "Vellum-ExeTest-" + Guid.NewGuid().ToString("N"));
        var gameDirectory = Path.Combine(root, "Game", "Binaries");
        Directory.CreateDirectory(gameDirectory);
        var setupPath = Path.Combine(root, "setup.exe");
        var gameExePath = Path.Combine(gameDirectory, "PlagueInc.exe");
        File.WriteAllBytes(setupPath, Array.Empty<byte>());
        File.WriteAllBytes(gameExePath, Array.Empty<byte>());

        try
        {
            Assert.Equal(gameExePath, GameExecutableLocator.FindMainExecutable(root));
            Assert.Equal(setupPath, GameExecutableLocator.FindSetupExecutable(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindMainExecutable_ReturnsNullWhenFolderContainsOnlySetupPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "Vellum-ExeTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "setup.exe"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(root, "payload.ftp"), Array.Empty<byte>());

        try
        {
            Assert.Null(GameExecutableLocator.FindMainExecutable(root));
            Assert.NotNull(GameExecutableLocator.FindSetupExecutable(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
