using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Services;
using Xunit;

namespace Nexora.Tests
{
    public class HttpDownloaderTests
    {
        [Fact]
        public async Task DownloadFileAsync_Fails_When_Connection_Closes_Early()
        {
            using var http = new HttpClient(new TruncatedContentHandler());
            var downloader = new HttpDownloader(http, NullLogger<HttpDownloader>.Instance);
            var destination = Path.Combine(
                Path.GetTempPath(), "NexoraHttpTruncated_" + Guid.NewGuid().ToString("N") + ".bin");

            try
            {
                var result = await downloader.DownloadFileAsync(
                    "https://example.test/file.bin", destination, "truncated-game");

                Assert.False(result.Success, "A truncated response must not be reported as a successful download.");
                Assert.Contains("closed early", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

                // The partial file is kept on purpose: the next attempt resumes from it.
                Assert.True(File.Exists(destination));
                Assert.Equal(10, new FileInfo(destination).Length);
            }
            finally
            {
                if (File.Exists(destination))
                    File.Delete(destination);
            }
        }

        [Fact]
        public async Task DownloadFileAsync_Succeeds_When_All_Bytes_Arrive()
        {
            using var http = new HttpClient(new FullContentHandler());
            var downloader = new HttpDownloader(http, NullLogger<HttpDownloader>.Instance);
            var destination = Path.Combine(
                Path.GetTempPath(), "NexoraHttpFull_" + Guid.NewGuid().ToString("N") + ".bin");

            try
            {
                var result = await downloader.DownloadFileAsync(
                    "https://example.test/file.bin", destination, "complete-game");

                Assert.True(result.Success, result.ErrorMessage);
                Assert.Equal(10, new FileInfo(destination).Length);
            }
            finally
            {
                if (File.Exists(destination))
                    File.Delete(destination);
            }
        }

        private sealed class TruncatedContentHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(CreateResponse(request, contentLength: 100, bytesToSend: 10));
        }

        private sealed class FullContentHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(CreateResponse(request, contentLength: 10, bytesToSend: 10));
        }

        private static HttpResponseMessage CreateResponse(
            HttpRequestMessage request, long contentLength, int bytesToSend)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new FixedSizeContent(bytesToSend, contentLength)
            };
        }

        private sealed class FixedSizeContent : HttpContent
        {
            private readonly byte[] _payload;
            private readonly long _declaredLength;

            public FixedSizeContent(int bytesToSend, long contentLength)
            {
                _payload = new byte[bytesToSend];
                _declaredLength = contentLength;
                Headers.ContentLength = contentLength;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => stream.WriteAsync(_payload, 0, _payload.Length);

            protected override Task SerializeToStreamAsync(
                Stream stream, TransportContext? context, CancellationToken cancellationToken)
                => stream.WriteAsync(_payload, 0, _payload.Length, cancellationToken);

            protected override bool TryComputeLength(out long length)
            {
                length = _declaredLength;
                return true;
            }
        }
    }
}
