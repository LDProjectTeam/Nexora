using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Nexora.Core.Services;

namespace Nexora.Tests
{
    public class GameSearchQueryMatcherTests
    {
        [Theory]
        [InlineData("cyberpk", "Cyberpunk 2077")]
        [InlineData("rdr2", "Red Dead Redemption 2")]
        [InlineData("witche", "The Witcher 3")]
        [InlineData("tlou", "The Last of Us")]
        public void MatchesTitleCharacters_MatchesPartialAndScatteredCharacters(string query, string title)
        {
            Assert.True(GameSearchQueryMatcher.MatchesTitleCharacters(query, title));
        }

        [Fact]
        public void MatchesTitleCharacters_DoesNotMatchCharactersOutOfOrder()
        {
            Assert.False(GameSearchQueryMatcher.MatchesTitleCharacters("punkcyber", "Cyberpunk 2077"));
        }

        [Theory]
        [InlineData("TLOU", "The Last of Us")]
        [InlineData("COD", "Call of Duty")]
        [InlineData("gta5", "Grand Theft Auto V")]
        public void GetQueryAlternatives_ExpandsCommonGameAbbreviations(string query, string expected)
        {
            var alternatives = GameSearchQueryMatcher.GetQueryAlternatives(query, System.Array.Empty<string>());

            Assert.Contains(expected, alternatives);
        }

        [Fact]
        public void GetQueryAlternatives_CorrectsSingleCharacterMisspelling()
        {
            var alternatives = GameSearchQueryMatcher.GetQueryAlternatives(
                "witche 3",
                new List<string> { "The Witcher 3: Wild Hunt" });

            Assert.Equal(new[] { "The Witcher 3: Wild Hunt" }, alternatives);
        }

        [Fact]
        public void GetQueryAlternatives_DoesNotGuessWhenTwoTitlesAreEquallyClose()
        {
            var alternatives = GameSearchQueryMatcher.GetQueryAlternatives(
                "last of u",
                new List<string> { "The Last of Us", "The Last of Us Part II" });

            Assert.Empty(alternatives);
        }
        [Theory]
        [InlineData("сщв", "cod")]
        [InlineData("еру цшесрук", "the witcher")]
        [InlineData("unf", "гта")]
        [InlineData("gta", "пеф")]
        public void ConvertKeyboardLayout_TranslatesCorrectlyBetweenRuAndEn(string input, string expected)
        {
            Assert.Equal(expected, GameSearchQueryMatcher.ConvertKeyboardLayout(input));
        }

        [Theory]
        [InlineData("сщв", "Call of Duty")]
        [InlineData("гта", "Grand Theft Auto")]
        [InlineData("еру цшесрук", "the witcher")]
        [InlineData("ведьмак 3", "The Witcher 3")]
        public void GetQueryAlternatives_HandlesLayoutAndRussianAliases(string query, string expectedSubstring)
        {
            var alternatives = GameSearchQueryMatcher.GetQueryAlternatives(query, Array.Empty<string>());
            Assert.Contains(alternatives, alt => alt.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData("ведьмак", "The Witcher 3: Wild Hunt")]
        [InlineData("witcher", "Ведьмак 3")]
        [InlineData("сщв", "Call of Duty: Modern Warfare")]
        [InlineData("гта", "Grand Theft Auto V")]
        public void MatchesTitleCharacters_MatchesCrossLanguageAndLayout(string query, string title)
        {
            Assert.True(GameSearchQueryMatcher.MatchesTitleCharacters(query, title));
        }

        [Theory]
        [InlineData("S.T.A.L.K.E.R. 2: Heart of Chornobyl", "STALKER 2 Heart of Chornobyl")]
        [InlineData("Call of Duty: Modern Warfare (2019)", "Call of Duty Modern Warfare 2019")]
        [InlineData("Assassin's Creed: Valhalla", "Assassin s Creed Valhalla")]
        public void RutorGameSource_SanitizesQueryForSphinx(string input, string expected)
        {
            Assert.Equal(expected, RutorGameSource.SanitizeSearchQuery(input));
        }

        [Fact]
        public async Task SearchAllSourcesAsync_ExpandsAbbreviationAndRetriesWithFullTitle()
        {
            var source = new QueryRecordingSource(query => query == "The Last of Us"
                ? new[] { new GameReleaseCandidate { CleanTitle = "The Last of Us", SourceId = "rutor" } }
                : Array.Empty<GameReleaseCandidate>());
            var aggregator = new GameSearchAggregator(new[] { source }, new TitleNormalizer(), null!,
                NullLogger<GameSearchAggregator>.Instance);

            var results = await aggregator.SearchAllSourcesAsync("TLOU");

            Assert.Contains(source.Queries, query => query == "The Last of Us");
            Assert.Contains(results, candidate => candidate.CleanTitle == "The Last of Us");
        }

        [Fact]
        public async Task SearchAllSourcesAsync_CorrectsTypoUsingRecentlyIndexedGameTitles()
        {
            var recentTitle = new GameReleaseCandidate
            {
                CleanTitle = "The Witcher 3: Wild Hunt",
                SourceId = "rutor"
            };
            var source = new QueryRecordingSource(query => query == "The Witcher 3: Wild Hunt"
                ? new[] { new GameReleaseCandidate { CleanTitle = "The Witcher 3: Wild Hunt", SourceId = "rutor" } }
                : Array.Empty<GameReleaseCandidate>(), new[] { recentTitle });
            var aggregator = new GameSearchAggregator(new[] { source }, new TitleNormalizer(), null!,
                NullLogger<GameSearchAggregator>.Instance);
            await aggregator.GetLatestGamesAsync();

            var results = await aggregator.SearchAllSourcesAsync("Witche 3");

            Assert.Contains(source.Queries, query => query == "The Witcher 3: Wild Hunt");
            Assert.Contains(results, candidate => candidate.CleanTitle == "The Witcher 3: Wild Hunt");
        }

        private sealed class QueryRecordingSource : IGameSource
        {
            private readonly Func<string, IReadOnlyList<GameReleaseCandidate>> _search;
            private readonly IReadOnlyList<GameReleaseCandidate> _latest;

            public QueryRecordingSource(
                Func<string, IReadOnlyList<GameReleaseCandidate>> search,
                IReadOnlyList<GameReleaseCandidate>? latest = null)
            {
                _search = search;
                _latest = latest ?? Array.Empty<GameReleaseCandidate>();
            }

            public List<string> Queries { get; } = new();
            public string SourceId => "rutor";
            public string DisplayName => "Rutor";
            public int Priority { get; set; } = 1;
            public bool IsEnabled { get; set; } = true;

            public Task<IReadOnlyList<GameReleaseCandidate>> SearchAsync(string query, CancellationToken ct = default)
            {
                Queries.Add(query);
                return Task.FromResult(_search(query));
            }

            public Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default)
                => Task.FromResult(_latest);
        }
    }
}
