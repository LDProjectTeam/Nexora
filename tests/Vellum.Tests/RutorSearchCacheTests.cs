using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Services;

namespace Vellum.Tests
{
    public class RutorSearchCacheTests
    {
        [Fact]
        public async Task SearchAsync_CachesResultsAndReturnsIndependentCandidates()
        {
            var handler = new CountingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var source = new RutorGameSource(httpClient, NullLogger<RutorGameSource>.Instance, new TitleNormalizer());

            var firstResults = await source.SearchAsync(" Example Game ");
            Assert.Single(firstResults);
            var originalId = firstResults[0].Id;
            firstResults[0].Id = "changed-by-caller";

            var cachedResults = await source.SearchAsync("example   game");

            Assert.Single(cachedResults);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(originalId, cachedResults[0].Id);
            Assert.NotSame(firstResults[0], cachedResults[0]);
        }

        private sealed class CountingHttpMessageHandler : HttpMessageHandler
        {
            private const string SearchResultHtml = """
                <div id="index">
                  <table><tbody><tr>
                    <td>
                      <a href="/torrent/12345">Example Game [Portable]</a>
                      <a href="magnet:?xt=urn:btih:0123456789ABCDEF&amp;dn=Example">magnet</a>
                    </td>
                    <td>1.2 GB</td>
                    <td><span class="green">24</span><span class="red">3</span></td>
                  </tr></tbody></table>
                </div>
                """;

            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SearchResultHtml, Encoding.UTF8, "text/html")
                });
            }
        }
    }
}
