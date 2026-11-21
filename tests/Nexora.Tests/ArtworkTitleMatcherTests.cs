using Nexora.Core.Services;
using Xunit;

namespace Nexora.Tests;

public class ArtworkTitleMatcherTests
{
    [Theory]
    [InlineData("Call of Duty: Ghosts - Ghosts Deluxe Edition", "Call of Duty: Ghosts", true)]
    [InlineData("Call of Duty: Ghosts", "Call of Duty: Modern Warfare II", false)]
    [InlineData("Call of Duty: Black Ops", "Call of Duty: Black Ops Cold War", false)]
    [InlineData("Call of Duty: Black Ops - Collection Edition", "Call of Duty: Black Ops", true)]
    [InlineData("The Forest", "Sons of the Forest", false)]
    [InlineData("The Forest", "The Forest Quartet", false)]
    [InlineData("The Forest (2014)", "The Forest", true)]
    [InlineData("The Long Dark: Quiet Apocalypse Bundle", "The Long Dark", true)]
    [InlineData("Metro Redux: Dilogy", "Metro Redux", true)]
    [InlineData("Spider-Man: Web of Shadows (2008)", "Spider-Man: Web of Shadows", true)]
    [InlineData("Bloons TD 6", "Bloons TD 6", true)]
    [InlineData("Bloons TD 6", "Bloons TD Battles", false)]
    [InlineData("FIFA 23", "FIFA 22", false)]
    public void IsCompatible_RejectsUnrelatedArtworkCandidates(
        string requested, string candidate, bool expected)
    {
        Assert.Equal(expected, ArtworkTitleMatcher.IsCompatible(requested, candidate));
    }
}
