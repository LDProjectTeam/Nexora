using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Microsoft.Extensions.Logging;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Orchestrates downloads. Tries the sources of a game in order of Priority
    /// (torrent / http / local), supports pause, resume and cancel.
    /// </summary>
    public class DownloadManager : IDownloadManager, IDisposable
    {
        /// <summary>Message returned by DownloadAsync when the download was paused.</summary>
        public const string PausedMessage = "Download paused.";
        /// <summary>Message returned by DownloadAsync when the download was cancelled.</summary>
        public const string CancelledMessage = "Download cancelled.";

        private const int BufferSize = 81920;

        private readonly ILogger<DownloadManager> _logger;
        private readonly RqbitDownloader _torrent;
        private readonly HttpDownloader _http;
        private readonly ISettingsService _settings;

        private sealed class SourceProgress : IProgress<DownloadProgress>
        {
            private readonly IProgress<DownloadProgress>? _inner;
            private readonly bool _isTorrent;

            public SourceProgress(IProgress<DownloadProgress>? inner, bool isTorrent)
            {
                _inner = inner;
                _isTorrent = isTorrent;
            }

            public void Report(DownloadProgress value)
            {
                value.IsTorrent = _isTorrent;
                _inner?.Report(value);
            }
        }

        private sealed class ActiveDownload
        {
            public ActiveDownload(GameDefinition game, string destFolder, CancellationTokenSource cts)
            {
                Game = game;
                DestFolder = destFolder;
                Cts = cts;
            }

            public GameDefinition Game { get; }
            public string DestFolder { get; }
            public CancellationTokenSource Cts { get; }
            public volatile bool PauseRequested;
            public volatile string? SourceType;
            public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // gameId -> running download
        private readonly ConcurrentDictionary<string, ActiveDownload> _active = new();
        // gameId -> paused download (what is needed to restart it)
        private readonly ConcurrentDictionary<string, (GameDefinition game, string destFolder)> _paused = new();

        private bool _disposed;

        public DownloadManager(
            ILogger<DownloadManager> logger,
            RqbitDownloader torrent,
            HttpDownloader http,
            ISettingsService settings)
        {
            _logger = logger;
            _torrent = torrent;
            _http = http;
            _settings = settings;
        }

        /// <summary>True if the download of this game is currently paused.</summary>
        public bool IsPaused(string gameId) => _paused.ContainsKey(gameId);

        /// <summary>True if the download of this game is currently running.</summary>
        public bool IsDownloading(string gameId) => _active.ContainsKey(gameId);

        public async Task<OperationResult> DownloadAsync(
            GameDefinition game,
            string destinationFolder,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken externalCt = default)
        {
            // A pause may still be unwinding (Pause then quickly Resume): wait for it to finish.
            if (_active.TryGetValue(game.Id, out var existing) && existing.PauseRequested)
            {
                try { await existing.Done.Task.WaitAsync(TimeSpan.FromSeconds(10), externalCt).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { return OperationResult.Fail(CancelledMessage); }
            }

            _paused.TryRemove(game.Id, out _);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var active = new ActiveDownload(game, destinationFolder, cts);

            if (!_active.TryAdd(game.Id, active))
            {
                cts.Dispose();
                return OperationResult.Fail("This game is already being downloaded.");
            }

            try
            {
                var sources = game.DownloadSources.OrderBy(s => s.Priority).ToList();
                if (sources.Count == 0)
                    return OperationResult.Fail("Game has no download sources.");

                string? lastFailureMessage = null;

                foreach (var source in sources)
                {
                    active.SourceType = source.Type;
                    var isTorrent = string.Equals(source.Type, "torrent", StringComparison.OrdinalIgnoreCase);
                    var sourceProgress = new SourceProgress(progress, isTorrent);
                    sourceProgress.Report(new DownloadProgress
                    {
                        GameId = game.Id,
                        Phase = "Downloading",
                        PhaseKey = DownloadPhases.Downloading
                    });

                    try
                    {
                        _logger.LogInformation("Trying source type '{Type}' for {GameId}", source.Type, game.Id);

                        OperationResult result = (source.Type ?? string.Empty).ToLowerInvariant() switch
                        {
                            "torrent" => await DownloadViaTorrentAsync(game, source, destinationFolder,
                                sourceProgress, cts.Token).ConfigureAwait(false),
                            "http" or "https" or "installer" => await DownloadViaHttpOrLocalAsync(game, source, destinationFolder,
                                sourceProgress, cts.Token).ConfigureAwait(false),
                            "local" or "file" => await DownloadViaLocalAsync(game, source, destinationFolder,
                                sourceProgress, cts.Token).ConfigureAwait(false),
                            _ => OperationResult.Fail($"Unknown source type: {source.Type}")
                        };

                        if (result.Success)
                            return result;

                        // Downloaders may return Fail instead of throwing when cancelled.
                        // In that case do NOT fall through to the next source.
                        if (cts.IsCancellationRequested)
                            return HandleCancellation(active);

                        lastFailureMessage = result.ErrorMessage;
                        _logger.LogWarning("Source {Type} failed for {GameId}: {Error}. Trying next source.",
                            source.Type, game.Id, result.ErrorMessage);
                    }
                    catch (OperationCanceledException)
                    {
                        return HandleCancellation(active);
                    }
                    catch (Exception ex)
                    {
                        lastFailureMessage = ex.Message;
                        _logger.LogError(ex, "Source {Type} threw for {GameId}. Trying next source.",
                            source.Type, game.Id);
                    }
                }

                return OperationResult.Fail(lastFailureMessage ?? "All download sources failed.");
            }
            finally
            {
                _active.TryRemove(game.Id, out _);
                cts.Dispose();
                active.Done.TrySetResult();
            }
        }

        private OperationResult HandleCancellation(ActiveDownload active)
        {
            if (active.PauseRequested)
            {
                _paused[active.Game.Id] = (active.Game, active.DestFolder);
                _logger.LogInformation("Download of {GameId} paused.", active.Game.Id);
                return OperationResult.Fail(PausedMessage);
            }

            _logger.LogInformation("Download of {GameId} cancelled.", active.Game.Id);
            return OperationResult.Fail(CancelledMessage);
        }

        private async Task<OperationResult> DownloadViaTorrentAsync(
            GameDefinition game, DownloadSource source, string destFolder,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            OperationResult? lastResult = null;

            // Prefer Magnet link first: it carries the original info_hash known to public DHT & active swarm
            // without custom tracker/private restrictions injected by mirror torrent generators.
            if (!string.IsNullOrEmpty(source.MagnetLink))
            {
                try
                {
                    _logger.LogInformation("Attempting magnet download for {GameId}", game.Id);
                    var magnetResult = await _torrent.DownloadAsync(
                        game.Id, source.MagnetLink, false, destFolder, progress,
                        _settings.Settings.MaxDownloadSpeedBps,
                        _settings.Settings.MaxUploadSpeedBps, ct, source.AdditionalTrackers).ConfigureAwait(false);

                    if (magnetResult.Success)
                        return magnetResult;

                    lastResult = magnetResult;

                    // If cancelled or paused, do NOT attempt fallback to torrent file
                    if (ct.IsCancellationRequested)
                        return magnetResult;

                    _logger.LogWarning("Magnet download failed ({Error}). Falling back to torrent file if available", magnetResult.ErrorMessage);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastResult = OperationResult.Fail($"Magnet download error: {ex.Message}");
                    _logger.LogWarning(ex, "Exception downloading via magnet for {GameId}. Falling back to torrent file if available", game.Id);
                }
            }

            if (!string.IsNullOrEmpty(source.TorrentFileUrl) && !ct.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Attempting torrent file download for {GameId}", game.Id);
                    var torrentResult = await _torrent.DownloadAsync(
                        game.Id, source.TorrentFileUrl, true, destFolder, progress,
                        _settings.Settings.MaxDownloadSpeedBps,
                        _settings.Settings.MaxUploadSpeedBps, ct, source.AdditionalTrackers).ConfigureAwait(false);

                    if (torrentResult.Success)
                        return torrentResult;

                    lastResult = torrentResult;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Exception downloading via torrent file for {GameId}", game.Id);
                    lastResult ??= OperationResult.Fail($"Torrent file error: {ex.Message}");
                }
            }

            return lastResult ?? OperationResult.Fail(Localizer.T("TorrentErrorInvalidSource", "Источник торрента не содержит действительной ссылки или магнет-ссылки."));
        }

        private async Task<OperationResult> DownloadViaHttpOrLocalAsync(
            GameDefinition game, DownloadSource source, string destFolder,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(source.Url))
                return OperationResult.Fail("HTTP source has no URL.");

            if (!source.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !source.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return await DownloadViaLocalAsync(game, source, destFolder, progress, ct).ConfigureAwait(false);
            }

            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
                return OperationResult.Fail($"Invalid URL: {source.Url}");

            var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
            if (string.IsNullOrEmpty(fileName))
                fileName = $"{game.Id}.download";

            Directory.CreateDirectory(destFolder);
            var destPath = Path.Combine(destFolder, fileName);

            return await _http.DownloadFileAsync(
                source.Url, destPath, game.Id, progress,
                _settings.Settings.MaxDownloadSpeedBps, ct).ConfigureAwait(false);
        }

        private async Task<OperationResult> DownloadViaLocalAsync(
            GameDefinition game, DownloadSource source, string destFolder,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var rawPath = source.Url ?? string.Empty;
            if (rawPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                rawPath = new Uri(rawPath).LocalPath;

            if (string.IsNullOrWhiteSpace(rawPath))
                return OperationResult.Fail("Local source has no path.");

            // Resolve relative paths against the working/app folders and their parents.
            // Absolute paths are used as-is. The old hardcoded "d:\launcher" fallback
            // made published builds depend on the developer's machine.
            string? localPath = Path.IsPathRooted(rawPath)
                ? (File.Exists(rawPath) ? rawPath : null)
                : PathHelper.FindFileWalkingUp(rawPath);

            if (localPath == null && !string.IsNullOrWhiteSpace(_settings.Settings.DefaultInstallPath))
            {
                var candidate = Path.Combine(_settings.Settings.DefaultInstallPath, rawPath);
                if (File.Exists(candidate))
                    localPath = candidate;
            }

            if (localPath == null)
                return OperationResult.Fail($"Local source file not found: {rawPath}");

            localPath = Path.GetFullPath(localPath);

            Directory.CreateDirectory(destFolder);
            var destPath = Path.Combine(destFolder, Path.GetFileName(localPath));
            var fileName = Path.GetFileName(localPath);

            long totalBytes = new FileInfo(localPath).Length;
            long copiedBytes = 0;
            long lastReportedBytes = 0;
            var progressClock = System.Diagnostics.Stopwatch.StartNew();
            bool completed = false;

            try
            {
                using (var srcStream = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                           FileShare.Read, BufferSize, true))
                using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                           FileShare.None, BufferSize, true))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await srcStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await destStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        copiedBytes += read;

                        if (progressClock.ElapsedMilliseconds >= 500 || copiedBytes == totalBytes)
                        {
                            var elapsedSeconds = Math.Max(progressClock.Elapsed.TotalSeconds, 0.001);
                            progress?.Report(new DownloadProgress
                            {
                                GameId = game.Id,
                                Fraction = totalBytes > 0 ? (double)copiedBytes / totalBytes : 1,
                                BytesDownloaded = copiedBytes,
                                TotalBytes = totalBytes,
                                SpeedBytesPerSec = (long)((copiedBytes - lastReportedBytes) / elapsedSeconds),
                                Phase = "Copying files",
                                PhaseKey = DownloadPhases.CopyingFiles,
                                CurrentFile = fileName
                            });
                            lastReportedBytes = copiedBytes;
                            progressClock.Restart();
                        }
                    }
                }

                completed = true;
                return OperationResult.Ok();
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to copy local file {Path}", localPath);
                return OperationResult.Fail($"Copy failed: {ex.Message}");
            }
            finally
            {
                // Remove the partial copy if we were interrupted (local copy can't resume).
                if (!completed)
                {
                    try { if (File.Exists(destPath)) File.Delete(destPath); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Could not delete partial file {Path}", destPath); }
                }
            }
        }

        public async Task PauseAsync(string gameId)
        {
            if (!_active.TryGetValue(gameId, out var active))
                return;

            active.PauseRequested = true;

            // Only torrents have an engine-side pause; HTTP/local just stop via the token.
            if (string.Equals(active.SourceType, "torrent", StringComparison.OrdinalIgnoreCase))
            {
                try { await _torrent.PauseAsync(gameId).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "torrent pause failed for {GameId}", gameId); }
            }

            try { active.Cts.Cancel(); }
            catch (ObjectDisposedException) { /* download already finished */ }
        }

        /// <summary>
        /// Restarts a paused download with the same game and destination.
        /// Completes when the download finishes (or is paused/cancelled again),
        /// so call it without await if you don't want to block.
        /// </summary>
        public async Task ResumeAsync(string gameId,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (!_paused.TryRemove(gameId, out var state))
            {
                _logger.LogWarning("Resume called for {GameId}, but it is not paused.", gameId);
                return;
            }

            _logger.LogInformation("Resuming download of {GameId}.", gameId);

            // Torrent engine and HTTP downloader resume from existing data internally.
            var result = await DownloadAsync(state.game, state.destFolder, progress, ct).ConfigureAwait(false);

            if (!result.Success && result.ErrorMessage != PausedMessage && result.ErrorMessage != CancelledMessage)
                _logger.LogWarning("Resumed download of {GameId} failed: {Error}", gameId, result.ErrorMessage);
        }

        public async Task CancelAsync(string gameId)
        {
            _paused.TryRemove(gameId, out _);

            if (_active.TryGetValue(gameId, out var active))
            {
                active.PauseRequested = false;
                try { active.Cts.Cancel(); }
                catch (ObjectDisposedException) { /* already finished */ }

                // Let the downloader finish its cancellation cleanup before removing
                // the torrent from the engine, bounded so a hung stream never blocks UI cleanup.
                try { await active.Done.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { _logger.LogWarning("Timed out waiting for active download {GameId} to cancel.", gameId); }
            }

            try { await _torrent.RemoveAsync(gameId).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "torrent remove failed for {GameId}", gameId); }
        }

        public async Task StopSeedingAsync(string gameId)
        {
            try { await _torrent.StopAsync(gameId).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "stop seeding failed for {GameId}", gameId); }
        }

        public async Task PauseAndSaveAsync(string gameId)
        {
            if (!_active.TryGetValue(gameId, out var active))
                return;

            active.PauseRequested = true;

            if (string.Equals(active.SourceType, "torrent", StringComparison.OrdinalIgnoreCase))
            {
                try { await _torrent.PauseAndSaveResumeAsync(gameId).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "torrent pause failed for {GameId}", gameId); }
            }

            try { active.Cts.Cancel(); }
            catch (ObjectDisposedException) { /* download already finished */ }

            try
            {
                await active.Done.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Wait for active download completion timed out for {GameId}", gameId);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var active in _active.Values)
            {
                active.PauseRequested = true;
                try { active.Cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            _paused.Clear();
            _torrent.Dispose();
        }
    }
}
