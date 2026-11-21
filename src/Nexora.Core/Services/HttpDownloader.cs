using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Downloads a file over HTTP/HTTPS with resume support, progress reporting,
    /// and optional speed limiting.
    /// </summary>
    public class HttpDownloader
    {
        private readonly HttpClient _http;
        private readonly ILogger<HttpDownloader> _logger;
        private const int BufferSize = 81920; // 80 KB

        /// <summary>A connection that sends nothing for this long is considered dead.</summary>
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

        public HttpDownloader(HttpClient http, ILogger<HttpDownloader> logger)
        {
            _http = http;
            _logger = logger;
        }

        /// <summary>
        /// Download a file to destinationPath. If the file already partially exists,
        /// will attempt to resume via Range header.
        /// </summary>
        public async Task<OperationResult> DownloadFileAsync(
            string url,
            string destinationPath,
            string gameId,
            IProgress<DownloadProgress>? progress = null,
            long maxSpeedBps = 0,
            CancellationToken ct = default)
        {
            try
            {
                var dir = Path.GetDirectoryName(destinationPath)!;
                Directory.CreateDirectory(dir);

                long existingBytes = 0;
                if (File.Exists(destinationPath))
                    existingBytes = new FileInfo(destinationPath).Length;

                // Use one GET for both size discovery and transfer; a separate HEAD adds an
                // unnecessary network round trip and is unsupported by many file hosts.
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var isResumeRequest = existingBytes > 0;
                if (isResumeRequest)
                    request.Headers.Range = new RangeHeaderValue(existingBytes, null);

                using var response = await _http.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var startBytes = 0L;
                var fileMode = FileMode.Create;

                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    var remoteLength = response.Content.Headers.ContentRange?.Length ?? -1;
                    if (remoteLength > 0 && existingBytes == remoteLength)
                    {
                        progress?.Report(new DownloadProgress
                        {
                            GameId = gameId, Fraction = 1.0, BytesDownloaded = remoteLength,
                            TotalBytes = remoteLength, Phase = "Downloading",
                            PhaseKey = DownloadPhases.Downloading
                        });
                        return OperationResult.Ok();
                    }

                    // The partial file is invalid for this server. Retry once from byte zero.
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp2 = await _http.SendAsync(req2,
                        HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    resp2.EnsureSuccessStatusCode();
                    var restartedTotal = resp2.Content.Headers.ContentLength ?? -1;
                    await DoDownloadAsync(resp2, destinationPath, fileMode,
                        gameId, 0, restartedTotal, progress, maxSpeedBps, ct).ConfigureAwait(false);
                    return OperationResult.Ok();
                }

                if (isResumeRequest && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    startBytes = existingBytes;
                    fileMode = FileMode.Append;
                    _logger.LogInformation("Resuming download from byte {Start}", existingBytes);
                }
                else
                    response.EnsureSuccessStatusCode(); // A 200 response means the host ignored Range; overwrite safely.

                var totalBytes = response.Content.Headers.ContentRange?.Length
                    ?? (response.Content.Headers.ContentLength is long contentLength ? startBytes + contentLength : -1);
                await DoDownloadAsync(response, destinationPath, fileMode,
                    gameId, startBytes, totalBytes, progress, maxSpeedBps, ct).ConfigureAwait(false);

                return OperationResult.Ok();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Download cancelled for {GameId}", gameId);
                return OperationResult.Fail("Download cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download failed for {GameId}: {Url}", gameId, url);
                return OperationResult.Fail($"Download failed: {ex.Message}", ex);
            }
        }

        private async Task DoDownloadAsync(
            HttpResponseMessage response,
            string destinationPath,
            FileMode fileMode,
            string gameId,
            long startBytes,
            long totalBytes,
            IProgress<DownloadProgress>? progress,
            long maxSpeedBps,
            CancellationToken ct)
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var file = new FileStream(destinationPath, fileMode, FileAccess.Write,
                FileShare.None, BufferSize, FileOptions.Asynchronous);

            var buffer = new byte[BufferSize];
            long downloaded = startBytes;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastReportBytes = startBytes;
            long smoothedSpeedBps = 0;
            var lastReportTime = sw.Elapsed;

            // CancelAfter() is re-armed on every read. Without this a half-open TCP
            // connection made the download hang forever (torrents already detect stalls).
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            while (true)
            {
                idleCts.CancelAfter(IdleTimeout);

                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException(
                        $"The server stopped sending data for {IdleTimeout.TotalSeconds:0} seconds.");
                }

                if (read <= 0)
                    break;

                await file.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                downloaded += read;

                var elapsed = sw.Elapsed;
                var timeSinceReport = (elapsed - lastReportTime).TotalSeconds;

                if (timeSinceReport >= 0.5 || downloaded == totalBytes)
                {
                    long speedBps = timeSinceReport > 0
                        ? (long)((downloaded - lastReportBytes) / timeSinceReport)
                        : 0;
                    if (speedBps > 0)
                        smoothedSpeedBps = smoothedSpeedBps == 0 ? speedBps : (long)(smoothedSpeedBps * 0.7 + speedBps * 0.3);
                    var etaSpeed = smoothedSpeedBps > 0 ? smoothedSpeedBps : speedBps;
                    long secsRemaining = etaSpeed > 0 && totalBytes > 0
                        ? (totalBytes - downloaded) / etaSpeed
                        : -1;

                    double fraction = totalBytes > 0 ? (double)downloaded / totalBytes : 0;

                    progress?.Report(new DownloadProgress
                    {
                        GameId = gameId,
                        Fraction = Math.Min(fraction, 1.0),
                        BytesDownloaded = downloaded,
                        TotalBytes = totalBytes,
                        SpeedBytesPerSec = speedBps,
                        SecondsRemaining = secsRemaining,
                        Phase = Localizer.T("TorrentPhaseDownloading", "Загрузка…"),
                        PhaseKey = DownloadPhases.Downloading
                    });

                    lastReportBytes = downloaded;
                    lastReportTime = elapsed;
                }

                // Simple speed limiter
                if (maxSpeedBps > 0 && sw.Elapsed.TotalSeconds > 0)
                {
                    var expectedTime = (double)downloaded / maxSpeedBps;
                    var actualTime = sw.Elapsed.TotalSeconds;
                    if (expectedTime > actualTime)
                    {
                        var delay = (int)((expectedTime - actualTime) * 1000);
                        if (delay > 0)
                            await Task.Delay(Math.Min(delay, 1000), ct).ConfigureAwait(false);
                    }
                }
            }

            // A graceful EOF may still be a truncated response (dropped connection,
            // proxy, lying Content-Length). Never report success for a partial file:
            // installing it would silently produce a broken game.
            if (totalBytes > 0 && downloaded < totalBytes)
            {
                throw new IOException(
                    $"The connection closed early: received {downloaded} of {totalBytes} bytes.");
            }
        }
    }
}
