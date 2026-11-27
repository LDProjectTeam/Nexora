using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Downloads changed files into a staging folder with bounded parallelism, an
    /// aggregate speed limit, Range resume and transient-error retries. The live game
    /// folder is never touched here.
    /// </summary>
    public class UpdateDownloadManager : IUpdateDownloadManager
    {
        private const int BufferSize = 80 * 1024;
        private const int MaxAttempts = 4;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

        private readonly HttpClient _http;
        private readonly IIntegrityVerifier _verifier;
        private readonly ILogger<UpdateDownloadManager> _logger;

        public UpdateDownloadManager(HttpClient http, IIntegrityVerifier verifier,
            ILogger<UpdateDownloadManager> logger)
        {
            _http = http;
            _verifier = verifier;
            _logger = logger;
        }

        public async Task<UpdateDownloadResult> DownloadAsync(
            GameUpdatePlan plan, string stagingRoot, long maxSpeedBps, int maxConcurrentFiles,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var files = plan.Diff.Downloads.ToList();
            var payloadRoot = Path.Combine(stagingRoot, "payload");
            var partialRoot = Path.Combine(stagingRoot, "partial");
            Directory.CreateDirectory(payloadRoot);
            Directory.CreateDirectory(partialRoot);
            if (files.Count == 0)
                return UpdateDownloadResult.Ok(0, 0);

            var total = files.Sum(f => f.Size);
            var state = new AggregateProgress(plan.GameId, total, progress);

            foreach (var file in files)
            {
                if (SafeRelativePath.TryResolve(payloadRoot, file.Path, out var finalPath)
                    && await _verifier.VerifyFileAsync(finalPath, file.Size, file.Sha256, ct).ConfigureAwait(false))
                {
                    state.AddCompleted(file.Size);
                }
            }

            using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var failures = new ConcurrentQueue<string>();
            var throttler = new SemaphoreSlim(Math.Clamp(maxConcurrentFiles, 1, 16));
            var limiter = new SharedRateLimiter(maxSpeedBps);

            var tasks = files.Select(async file =>
            {
                await throttler.WaitAsync(failureCts.Token).ConfigureAwait(false);
                try
                {
                    if (!SafeRelativePath.TryResolve(payloadRoot, file.Path, out var finalPath))
                        throw new InvalidDataException($"Unsafe update path '{file.Path}'.");
                    if (File.Exists(finalPath))
                    {
                        if (await _verifier.VerifyFileAsync(finalPath, file.Size, file.Sha256, ct).ConfigureAwait(false))
                            return;
                        TryDelete(finalPath);
                    }

                    if (!SafeRelativePath.TryResolve(partialRoot, file.Path, out var partialBase))
                        throw new InvalidDataException($"Unsafe update path '{file.Path}'.");
                    var partialPath = partialBase + ".part";
                    Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);

                    await DownloadFileWithRetriesAsync(file, partialPath, limiter, state, failureCts.Token)
                        .ConfigureAwait(false);

                    if (!await _verifier.VerifyFileAsync(partialPath, file.Size, file.Sha256, failureCts.Token)
                            .ConfigureAwait(false))
                    {
                        TryDelete(partialPath);
                        throw new InvalidDataException(
                            $"Downloaded file '{file.Path}' failed SHA-256 verification.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    File.Move(partialPath, finalPath, overwrite: true);
                }
                catch (OperationCanceledException) when (failureCts.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Download of {File} failed", file.Path);
                    failures.Enqueue(file.Path + ": " + ex.Message);
                    failureCts.Cancel();
                }
                finally
                {
                    throttler.Release();
                }
            }).ToList();

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                state.Stop();
                return UpdateDownloadResult.PausedAt(state.DownloadedBytes, total);
            }

            state.Stop();
            if (ct.IsCancellationRequested)
                return UpdateDownloadResult.PausedAt(state.DownloadedBytes, total);

            if (!failures.IsEmpty)
            {
                var message = string.Join("; ", failures.Take(3));
                if (!NetworkInterface.GetIsNetworkAvailable())
                    return UpdateDownloadResult.PausedAt(state.DownloadedBytes, total);
                return UpdateDownloadResult.Fail(message, state.DownloadedBytes, total);
            }

            return UpdateDownloadResult.Ok(state.DownloadedBytes, total);
        }

        private async Task DownloadFileWithRetriesAsync(FileChange file, string partialPath,
            SharedRateLimiter limiter, AggregateProgress aggregate, CancellationToken ct)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadOnceAsync(file, partialPath, limiter, aggregate, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PermanentDownloadException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    last = ex;
                    if (!NetworkInterface.GetIsNetworkAvailable())
                        throw new IOException("No internet connection.", ex);
                    if (attempt + 1 >= MaxAttempts)
                        break;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger.LogWarning("Transient download error for {File} ({Attempt}/{Max}): {Message}",
                        file.Path, attempt + 1, MaxAttempts, ex.Message);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
            throw new IOException(
                $"Could not download '{file.Path}' after {MaxAttempts} attempts: {last?.Message}", last);
        }

        private async Task DownloadOnceAsync(FileChange file, string partialPath,
            SharedRateLimiter limiter, AggregateProgress aggregate, CancellationToken ct)
        {
            long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (existing > file.Size)
            {
                TryDelete(partialPath);
                existing = 0;
            }
            if (existing == file.Size && file.Size > 0)
            {
                aggregate.AddCompleted(existing);
                return;
            }

            aggregate.ReportPartial(file.Path, existing);

            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                && existing > 0 && response.Content.Headers.ContentRange?.Length == existing)
            {
                aggregate.AddCompleted(existing);
                return;
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                or HttpStatusCode.Unauthorized)
            {
                throw new PermanentDownloadException(
                    $"The update server returned {(int)response.StatusCode} for '{file.Path}'.");
            }

            if (response.StatusCode == HttpStatusCode.PartialContent && existing > 0)
            {
                // Range accepted, append to the partial file.
            }
            else
            {
                response.EnsureSuccessStatusCode();
                existing = 0;
                TryDelete(partialPath);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(partialPath, existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);

            var buffer = new byte[BufferSize];
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            long writtenThisRun = 0;
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
                        $"The update server stopped sending data for {IdleTimeout.TotalSeconds:0} seconds.");
                }

                if (read <= 0)
                    break;
                await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                writtenThisRun += read;
                aggregate.AddDelta(read, file.Path);
                await limiter.WaitAsync(read, ct).ConfigureAwait(false);
            }

            var finalSize = existing + writtenThisRun;
            if (finalSize < file.Size)
            {
                throw new IOException(
                    $"The connection closed early: received {finalSize} of {file.Size} bytes for '{file.Path}'.");
            }
            if (finalSize > file.Size)
            {
                TryDelete(partialPath);
                throw new InvalidDataException(
                    $"The server returned more data than expected for '{file.Path}'.");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class AggregateProgress
        {
            private readonly object _sync = new();
            private readonly string _gameId;
            private readonly long _total;
            private readonly IProgress<DownloadProgress>? _progress;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly HashSet<string> _partials = new(StringComparer.OrdinalIgnoreCase);
            private long _completed;
            private long _partial;
            private long _lastBytes;
            private TimeSpan _lastTime;
            private long _smoothedSpeed;
            private string _currentFile = string.Empty;
            private bool _stopped;

            public AggregateProgress(string gameId, long total, IProgress<DownloadProgress>? progress)
            {
                _gameId = gameId;
                _total = total;
                _progress = progress;
            }

            public long DownloadedBytes { get { lock (_sync) return _completed + _partial; } }

            public void AddCompleted(long bytes)
            {
                lock (_sync) { _completed += bytes; ReportLocked(); }
            }

            public void ReportPartial(string file, long existing)
            {
                lock (_sync)
                {
                    if (_partials.Add(file))
                        _partial += existing;
                    _currentFile = file;
                    ReportLocked();
                }
            }

            public void AddDelta(long bytes, string file)
            {
                lock (_sync)
                {
                    if (_stopped) return;
                    _partial += bytes;
                    _currentFile = file;
                    ReportLocked();
                }
            }

            public void Stop()
            {
                lock (_sync) { _stopped = true; ReportLocked(force: true); }
            }

            private void ReportLocked(bool force = false)
            {
                if (_progress == null)
                    return;
                var now = _clock.Elapsed;
                var since = now - _lastTime;
                if (!force && since.TotalMilliseconds < 350)
                    return;

                var downloaded = _completed + _partial;
                long speed = since.TotalSeconds > 0
                    ? (long)((downloaded - _lastBytes) / since.TotalSeconds)
                    : 0;
                if (speed > 0)
                    _smoothedSpeed = _smoothedSpeed == 0
                        ? speed
                        : (long)(_smoothedSpeed * 0.7 + speed * 0.3);

                var etaSpeed = _smoothedSpeed > 0 ? _smoothedSpeed : speed;
                _lastBytes = downloaded;
                _lastTime = now;

                _progress.Report(new DownloadProgress
                {
                    GameId = _gameId,
                    Fraction = _total > 0 ? Math.Clamp((double)downloaded / _total, 0.0, 1.0) : 0,
                    BytesDownloaded = downloaded,
                    TotalBytes = _total,
                    SpeedBytesPerSec = Math.Max(0, _smoothedSpeed),
                    SecondsRemaining = etaSpeed > 0 && _total > downloaded
                        ? (long)((_total - downloaded) / etaSpeed)
                        : -1,
                    CurrentFile = _currentFile,
                    Phase = Localizer.T("UpdatePhaseDownloading", "Downloading update…"),
                    PhaseKey = DownloadPhases.Downloading,
                    IsTorrent = false
                });
            }
        }

        private sealed class SharedRateLimiter
        {
            private readonly object _sync = new();
            private readonly long _bytesPerSecond;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private long _granted;

            public SharedRateLimiter(long bytesPerSecond) => _bytesPerSecond = bytesPerSecond;

            public Task WaitAsync(int bytes, CancellationToken ct)
            {
                if (_bytesPerSecond <= 0 || bytes <= 0)
                    return Task.CompletedTask;
                long delayMs;
                lock (_sync)
                {
                    _granted += bytes;
                    var allowed = (double)_granted / _bytesPerSecond;
                    delayMs = (long)Math.Max(0, (allowed - _clock.Elapsed.TotalSeconds) * 1000);
                }
                return delayMs > 0 ? Task.Delay((int)Math.Min(delayMs, 2000), ct) : Task.CompletedTask;
            }
        }

        private sealed class PermanentDownloadException : Exception
        {
            public PermanentDownloadException(string message) : base(message) { }
        }
    }
}
