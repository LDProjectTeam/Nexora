using Nexora.Core.Helpers;

namespace Nexora.Tests;

public class PathHelperTests
{
    [Fact]
    public void CleanupRemovesOnlyTheRequestedGamesStagingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-StagingTest-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "Library");
        var currentGame = PathHelper.GetStagingPath(library, "game-one");
        var otherGame = PathHelper.GetStagingPath(library, "game-two");
        Directory.CreateDirectory(currentGame);
        Directory.CreateDirectory(otherGame);
        File.WriteAllText(Path.Combine(currentGame, "download.bin"), "current game");
        File.WriteAllText(Path.Combine(otherGame, "download.bin"), "other game");

        try
        {
            Assert.True(PathHelper.DeleteGameStagingDirectory(currentGame, "game-one"));

            Assert.False(Directory.Exists(currentGame));
            Assert.True(File.Exists(Path.Combine(otherGame, "download.bin")));
            Assert.True(Directory.Exists(Path.GetDirectoryName(currentGame)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanupRejectsAPathOutsideTheGamesTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-StagingTest-" + Guid.NewGuid().ToString("N"));
        var unrelated = Path.Combine(root, "important-folder");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");

        try
        {
            Assert.False(PathHelper.DeleteGameStagingDirectory(unrelated, "game-one"));
            Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PayloadBytesOnDisk_CountsFilesAlreadyAllocatedForTheDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-PayloadTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllBytes(Path.Combine(root, "part1.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(root, "sub", "part2.bin"), new byte[2048]);

        try
        {
            // Without preallocation the files on disk are all that already occupies space.
            Assert.Equal(3072, PathHelper.GetPayloadBytesOnDisk(root, 0));
            // A preallocated torrent occupies its final size even if fewer pieces arrived,
            // and the persisted piece counter must never reduce the measured value.
            Assert.Equal(4096, PathHelper.GetPayloadBytesOnDisk(root, 4096));
            Assert.Equal(3072, PathHelper.GetPayloadBytesOnDisk(root, 2048));
            // A missing folder falls back to the persisted counter.
            Assert.Equal(2048, PathHelper.GetPayloadBytesOnDisk(
                Path.Combine(root, "missing"), 2048));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteEmptyDirectory_RemovesEmptyDirAndPreservesNonEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-EmptyDirTest-" + Guid.NewGuid().ToString("N"));
        var emptyDir = Path.Combine(root, "EmptyDir");
        var nonEmptyDir = Path.Combine(root, "NonEmptyDir");
        Directory.CreateDirectory(emptyDir);
        Directory.CreateDirectory(nonEmptyDir);
        File.WriteAllText(Path.Combine(nonEmptyDir, "file.txt"), "data");

        try
        {
            Assert.True(PathHelper.DeleteEmptyDirectory(emptyDir));
            Assert.False(Directory.Exists(emptyDir));

            Assert.False(PathHelper.DeleteEmptyDirectory(nonEmptyDir));
            Assert.True(Directory.Exists(nonEmptyDir));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteEmptyStagingRoot_RemovesTempDirectoryWhenEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-StagingRootTest-" + Guid.NewGuid().ToString("N"));
        var tempDir = Path.Combine(root, "temp");
        Directory.CreateDirectory(tempDir);

        try
        {
            PathHelper.DeleteEmptyStagingRoot(tempDir);
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DefaultLibraryPath_LivesOutsideTheApplicationFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            return;

        var library = Path.GetFullPath(PathHelper.DefaultLibraryPath);
        Assert.StartsWith(Path.GetFullPath(profile), library, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.Combine(Path.GetFullPath(profile), "Games"),
            library,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindFileWalkingUp_ResolvesRelativeFilesAndRejectsAbsoluteMisses()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nexora-WalkTest-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        var probe = Path.Combine(dataDir, "probe.txt");
        File.WriteAllText(probe, "probe");

        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var resolved = PathHelper.FindFileWalkingUp(Path.Combine("data", "probe.txt"));
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(probe), resolved);

            Assert.Equal(Path.GetFullPath(probe), PathHelper.FindFileWalkingUp(probe));
            Assert.Null(PathHelper.FindFileWalkingUp(Path.Combine(root, "missing.txt")));
            Assert.Null(PathHelper.FindFileWalkingUp(null!));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsSafeToDeleteTree_RejectsDangerousPathsAndRespectsTheLibraryRoot()
    {
        Assert.False(PathHelper.IsSafeToDeleteTree(null));
        Assert.False(PathHelper.IsSafeToDeleteTree(string.Empty));
        Assert.False(PathHelper.IsSafeToDeleteTree(Path.GetPathRoot(Path.GetTempPath())));
        Assert.False(PathHelper.IsSafeToDeleteTree(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(PathHelper.IsSafeToDeleteTree(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(PathHelper.IsSafeToDeleteTree(AppDomain.CurrentDomain.BaseDirectory));

        var library = Path.Combine(Path.GetTempPath(), "Nexora-SafetyTest-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(library, "SomeGame");
        var outside = Path.Combine(Path.GetTempPath(), "Nexora-Outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(outside);

        try
        {
            // A normal game folder inside the library is deletable...
            Assert.True(PathHelper.IsSafeToDeleteTree(game));
            Assert.True(PathHelper.IsSafeToDeleteTree(game, library));

            // ...but the library root itself and folders outside it are not.
            Assert.False(PathHelper.IsSafeToDeleteTree(library, library));
            Assert.False(PathHelper.IsSafeToDeleteTree(outside, library));
        }
        finally
        {
            if (Directory.Exists(library))
                Directory.Delete(library, recursive: true);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }
}
