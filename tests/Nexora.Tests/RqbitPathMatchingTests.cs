using Nexora.Core.Services;
using Xunit;

namespace Nexora.Tests
{
    public class RqbitPathMatchingTests
    {
        [Theory]
        [InlineData("Library/temp/doom", "doom", true)]
        [InlineData("Library/temp/DOOM", "doom", true)]
        [InlineData("Library/temp/doom-eternal", "doom", false)]
        [InlineData("Library/doom/data", "doom", true)]
        [InlineData("Library/doometernal", "doom", false)]
        [InlineData("Library/temp/other", "doom", false)]
        public void PathHasSegment_MatchesWholeSegmentsOnly(string path, string segment, bool expected)
        {
            // Regression guard: forgetting a torrent used to use outputFolder.IndexOf(gameId),
            // so "doom" could forget the torrent of "doom-eternal".
            Assert.Equal(expected, RqbitDownloader.PathHasSegment(path, segment));
        }

        [Fact]
        public void PathHasSegment_RejectsEmptyInput()
        {
            Assert.False(RqbitDownloader.PathHasSegment(string.Empty, "doom"));
            Assert.False(RqbitDownloader.PathHasSegment("Library/temp/doom", string.Empty));
        }
    }
}
