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
}
