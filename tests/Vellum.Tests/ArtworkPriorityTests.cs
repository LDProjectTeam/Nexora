using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;
using SkiaSharp;
using Xunit;

namespace Vellum.Tests;

public class ArtworkPriorityTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private static byte[] CreateJpegBytes()
    {
        const int width = 480;
        const int height = 270;
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new SKColor[width * height];
        var random = new Random(1234);
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        bitmap.Pixels = pixels;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    [Fact]
    public async Task ReleasePage_Artwork_Is_Scraped_Successfully()
    {
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <table>
    <tr>
      <td>
        <a href=""/""><img src=""//cdnbunny.org/logo.jpg"" alt=""logo"" /></a>
        <center><img src=""https://example.com/poster.jpg"" /></center>
      </td>
    </tr>
  </table>
</body>
</html>";

        var fakeImageData = CreateJpegBytes();

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("rutor.info") || req.RequestUri.Host.Contains("example.org"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                };
            }
            if (req.RequestUri.AbsoluteUri.Contains("poster.jpg"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fakeImageData)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var http = new HttpClient(handler);
        var service = new SteamArtworkService(http);

        var artwork = await service.FindArtworkAsync(
            "Custom Game 2026",
            "https://rutor.info/torrent/12345/custom-game-2026",
            ArtworkPriority.ModalDetails,
            forceRefresh: true);

        Assert.NotNull(artwork);
        Assert.False(string.IsNullOrWhiteSpace(artwork.CoverPath));
        Assert.True(File.Exists(artwork.CoverPath));
    }

    [Fact]
    public async Task ModalDetails_Priority_Deletes_MissFile_And_Retries()
    {
        var title = "MissTestGame " + Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(title.ToLowerInvariant()))).ToLowerInvariant();
        var cacheDir = Path.Combine(PathHelper.AppDataDir, "artwork");
        Directory.CreateDirectory(cacheDir);
        var missPath = Path.Combine(cacheDir, hash + "_v20.json.miss");
        File.WriteAllText(missPath, DateTime.UtcNow.ToString("O"));

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler);
        var service = new SteamArtworkService(http);

        // Background call sees miss and returns null
        var bgArtwork = await service.FindArtworkAsync(title, null, ArtworkPriority.Background, forceRefresh: false);
        Assert.Null(bgArtwork);

        // ModalDetails priority deletes miss file
        var detailsArtwork = await service.FindArtworkAsync(title, null, ArtworkPriority.ModalDetails, forceRefresh: true);
        Assert.Null(detailsArtwork);
        // ModalDetails never leaves a miss file behind
        Assert.False(File.Exists(missPath));
    }
}
