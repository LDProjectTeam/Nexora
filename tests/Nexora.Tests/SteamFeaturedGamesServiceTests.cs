using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Nexora.Core.Services;

namespace Nexora.Tests
{
    public class SteamFeaturedGamesServiceTests
    {
        [Fact]
        public async Task GetFeaturedGamesAsync_SkipsUnmatchedSteamGamesAndContinuesAcrossPages()
        {
            var requestedStarts = new List<string>();
            var handler = new StubHttpMessageHandler(request =>
            {
                var query = request.RequestUri!.Query;
                requestedStarts.Add(query.Contains("start=24", StringComparison.Ordinal) ? "24" : "0");
                var start = query.Contains("start=24", StringComparison.Ordinal) ? 24 : 0;
                var count = start == 0 ? 24 : 10;
                var items = Enumerable.Range(start + 1, count).Select(appId => new
                {
                    name = $"{(start == 0 ? "Unavailable" : "Popular Game")} {appId}",
                    logo = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_sm_120.jpg"
                });
                var json = JsonSerializer.Serialize(new { items, total_count = 34 });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            });
            var rutor = new StubGameSource(query =>
            {
                if (query.StartsWith("Popular Game ", StringComparison.Ordinal))
                {
                    return new[]
                    {
                        new GameReleaseCandidate
                        {
                            Id = "rutor-" + query,
                            RawTitle = query + " [Repack]",
                            CleanTitle = query,
                            SourceId = "rutor",
                            SourceName = "Rutor",
                            VersionType = GameVersionType.Repack
                        }
                    };
                }

                return new[]
                {
                    new GameReleaseCandidate
                    {
                        RawTitle = "An unrelated release",
                        CleanTitle = "An unrelated release",
                        SourceId = "rutor"
                    }
                };
            });
            using var httpClient = new HttpClient(handler);
            var service = new SteamFeaturedGamesService(httpClient, new[] { rutor },
                new TitleNormalizer(), NullLogger<SteamFeaturedGamesService>.Instance);

            var featured = await service.GetFeaturedGamesAsync();

            Assert.Equal(10, featured.Count);
            Assert.Equal(10, featured.Select(game => game.SteamGame.AppId).Distinct().Count());
            Assert.Equal("Popular Game 25", featured[0].SteamGame.Name);
            Assert.Equal("Popular Game 34", featured[^1].SteamGame.Name);
            Assert.Equal(
                "https://cdn.akamai.steamstatic.com/steam/apps/25/header.jpg",
                featured[0].SteamGame.CoverUrl);
            Assert.Equal(
                "https://cdn.akamai.steamstatic.com/steam/apps/25/library_hero.jpg",
                featured[0].SteamGame.BannerUrl);
            Assert.Equal(new[] { "0", "24" }, requestedStarts);
            Assert.Equal(34, rutor.SearchCount);
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

            public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            {
                _responseFactory = responseFactory;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(_responseFactory(request));
        }

        private sealed class StubGameSource : IGameSource
        {
            private readonly Func<string, IReadOnlyList<GameReleaseCandidate>> _search;

            public StubGameSource(Func<string, IReadOnlyList<GameReleaseCandidate>> search)
            {
                _search = search;
            }

            public string SourceId => "rutor";
            public string DisplayName => "Rutor";
            public int Priority { get; set; } = 1;
            public bool IsEnabled { get; set; } = true;
            public int SearchCount { get; private set; }

            public Task<IReadOnlyList<GameReleaseCandidate>> SearchAsync(
                string query,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                SearchCount++;
                return Task.FromResult(_search(query));
            }

            public Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<GameReleaseCandidate>>(Array.Empty<GameReleaseCandidate>());
        }
    }
}
