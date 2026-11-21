using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    /// <summary>
    /// BitTorrent downloader powered by Rust librqbit running as a high-performance,
    /// crash-isolated sidecar daemon with a local REST API.
    /// Provides pure Rust networking (DHT, µTP, UDP/HTTP trackers, fastresume, UPnP).
    /// </summary>
    public sealed class RqbitDownloader : ITorrentDownloader
    {
        private readonly ILogger<RqbitDownloader> _logger;
        private readonly HttpClient _http;
        private readonly ISettingsService? _settings;
        private readonly object _processLock = new();
        private readonly ConcurrentDictionary<string, int> _torrentIds = new(StringComparer.OrdinalIgnoreCase);
        // Games for which the user asked for a pause. Used to keep the automatic
        // "resume if paused" nudge from undoing a deliberate pause while the polling
        // loop is still unwinding.
        private readonly ConcurrentDictionary<string, byte> _pauseRequested = new(StringComparer.OrdinalIgnoreCase);

        private Process? _serverProcess;
        private int _serverPort;
        private string _apiBaseUrl = string.Empty;
        private bool _disposed;
        /// <summary>Upload rate limit (B/s) that the current sidecar was started with.</summary>
        private long _runningUploadBps = -1;

        private const int PollIntervalMs = 500;
        private const int SlowPollIntervalMs = 2000;
        private const int DefaultPort = 3030;
        private const int PeerPortStart = 4240;
        private const int PeerPortEnd = 4249;

        // Process priority lifting
        private readonly object _priorityLock = new();
        private int _activeDownloads;
        private bool _priorityRaised;
        private ProcessPriorityClass _prevPriority;
        private Process? _priorityProcess;

        public RqbitDownloader(ILogger<RqbitDownloader> logger, HttpClient http, ISettingsService? settings = null)
        {
            _logger = logger;
            _http = http;
            _settings = settings;
        }

        /// <summary>
        /// Gets the active HTTP API port, or 0 if not running.
        /// </summary>
        public int ServerPort => _serverPort;

        /// <summary>
        /// True if the sidecar process is currently alive and responding.
        /// </summary>
        public bool IsServerRunning => _serverProcess is { HasExited: false };

        // ─────────────────────────────────────────────────────────────────
        // Process Management
        // ─────────────────────────────────────────────────────────────────

        private async Task EnsureServerRunningAsync(long maxDownloadBps, long maxUploadBps, CancellationToken ct)
        {
            // Health check: use /torrents (GET / returns 404 in rqbit 9.x)
            if (_serverProcess is { HasExited: false } && !string.IsNullOrEmpty(_apiBaseUrl))
            {
                // If the upload rate limit changed (e.g. AllowSeeding toggled), the
                // running sidecar has a stale --ratelimit-upload. We must restart it
                // so the new limit takes effect. Only do this when no torrents are
                // actively downloading to avoid interrupting transfers.
                bool needsRestart = _runningUploadBps >= 0 && _runningUploadBps != maxUploadBps
                                    && _torrentIds.IsEmpty;

                if (!needsRestart)
                {
                    try
                    {
                        using var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var pingResp = await _http.GetAsync($"{_apiBaseUrl}/torrents", pingCts.Token).ConfigureAwait(false);
                        if (pingResp.IsSuccessStatusCode)
                            return;
                    }
                    catch
                    {
                        // Server might have stopped responding — restart below
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "Upload rate limit changed ({Old} -> {New} B/s); restarting sidecar",
                        _runningUploadBps, maxUploadBps);
                    lock (_processLock)
                    {
                        try { _serverProcess?.Kill(entireProcessTree: true); } catch { }
                        _serverProcess?.Dispose();
                        _serverProcess = null;
                        _apiBaseUrl = string.Empty;
                    }
                }
            }

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var recentLogs = new ConcurrentQueue<string>();
                lock (_processLock)
                {
                    // Double-checked: another thread may have already started the process.
                    if (_serverProcess is { HasExited: false } && !string.IsNullOrEmpty(_apiBaseUrl))
                        return;

                    // Kill stale process if it has exited
                    if (_serverProcess != null)
                    {
                        try { _serverProcess.Kill(entireProcessTree: true); } catch { }
                        _serverProcess.Dispose();
                        _serverProcess = null;
                    }

                    var exePath = LocateRqbitExecutable();
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    {
                        throw new FileNotFoundException(
                            "Could not locate rqbit executable. " +
                            "Ensure Nexora.TorrentEngine.exe (or rqbit.exe) is next to Nexora.exe or rqbit.exe is in tools\\bin\\. " +
                            "Set env var GAMEVAULT_RQBIT_PATH to override.");
                    }

                    // A previous launcher instance may have crashed and left its sidecar
                    // running. Two engines writing the same files make Continue look broken,
                    // so clean up orphaned instances of our own engine first.
                    KillStaleTorrentEngines(exePath);

                    _serverPort = GetAvailablePort(DefaultPort);
                    var peerPort = GetAvailablePeerPort(out var peerPortInReservedRange);
                    if (!peerPortInReservedRange)
                    {
                        _logger.LogWarning(
                            "Torrent peer port {Port} is outside the installed firewall range 4240-4249; " +
                            "inbound peers may be blocked until the rule covers this port.",
                            peerPort);
                    }
                    _apiBaseUrl = $"http://127.0.0.1:{_serverPort}";

                    var dataDir = PathHelper.TorrentDataDir;
                    PathHelper.EnsureDirectory(dataDir);
                    var defaultDownloadDir = Path.Combine(dataDir, "downloads");
                    PathHelper.EnsureDirectory(defaultDownloadDir);

                    // Ensure session.json is valid, has no UTF-8 BOM, and has no stale torrent records
                    SanitizeSessionFile(dataDir, _logger);
                    CleanStaleSessionEntries(dataDir);

                    // Build CLI: global flags first, then subcommand.
                    // rqbit [GLOBAL_FLAGS] server start <OUTPUT_FOLDER> [SERVER_FLAGS]
                    var args = new StringBuilder();

                    // Global flags (before subcommand)
                    args.Append($"--http-api-listen-addr 127.0.0.1:{_serverPort} ");
                    args.Append("--experimental-enable-utp-listen ");
                    args.Append($"--listen-port {peerPort} ");
                    // Keep a healthy peer pool per torrent. The engine default is deliberately
                    // conservative and can leave fast, well-seeded swarms underutilized.
                    args.Append("--peer-limit 100 ");
                    // Don't override tracker announce intervals too aggressively: the magnet
                    // links below may contain many trackers, and a 2-minute forced refresh
                    // creates unnecessary announce traffic and can get clients throttled.
                    args.Append("--tracker-refresh-interval 10m ");
                    if (maxDownloadBps > 0)
                        args.Append($"--ratelimit-download {maxDownloadBps} ");
                    if (maxUploadBps > 0)
                        args.Append($"--ratelimit-upload {maxUploadBps} ");

                    // Subcommand + positional + server-specific flags
                    args.Append("server start ");
                    args.Append($"\"{defaultDownloadDir}\" ");
                    args.Append("--fastresume ");
                    args.Append($"--persistence-location \"{dataDir}\"");

                    var psi = new ProcessStartInfo(exePath, args.ToString())
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        // Use the exe's own directory so relative paths inside rqbit resolve correctly
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
                    };

                    _logger.LogInformation(
                        "Starting rqbit sidecar (attempt {Attempt}): {Exe} {Args}; peer listen port {PeerPort}",
                        attempt, exePath, args.ToString(), peerPort);

                    var proc = Process.Start(psi)
                        ?? throw new InvalidOperationException("Failed to launch rqbit process.");

                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            _logger.LogTrace("[rqbit] {Line}", e.Data);
                            recentLogs.Enqueue(e.Data);
                            while (recentLogs.Count > 20) recentLogs.TryDequeue(out var _);
                        }
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            _logger.LogDebug("[rqbit:err] {Line}", e.Data);
                            recentLogs.Enqueue(e.Data);
                            while (recentLogs.Count > 20) recentLogs.TryDequeue(out var _);
                        }
                    };

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    _serverProcess = proc;

                    // Record the sidecar PID so the next launcher run can reap exactly this
                    // process if we crash and leave it behind.
                    try
                    {
                        PathHelper.EnsureDirectory(PathHelper.TorrentDataDir);
                        File.WriteAllText(SidecarPidFile, proc.Id.ToString());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not write sidecar PID file");
                    }
                }

                // Wait for API readiness — poll /torrents (up to 15 seconds)
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    var local = _serverProcess;
                    if (local == null || local.HasExited)
                        break;

                    try
                    {
                        using var testCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                        var resp = await _http.GetAsync($"{_apiBaseUrl}/torrents", testCts.Token).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("rqbit API ready at {Url}", _apiBaseUrl);
                            _runningUploadBps = maxUploadBps;
                            await ReconcileRestoredTorrentsAsync(resp, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch
                    {
                        // Not ready yet
                    }

                    await Task.Delay(300, ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();

                // If exited prematurely and attempt == 1, reset session file and try self-healing once
                var procExited = _serverProcess;
                if (procExited != null && procExited.HasExited && attempt == 1)
                {
                    var logs = string.Join(" | ", recentLogs.ToArray().TakeLast(5));
                    _logger.LogWarning("rqbit process exited prematurely on startup (code {Code}: {Logs}). Attempting recovery reset...",
                        procExited.ExitCode, logs);

                    lock (_processLock)
                    {
                        try { procExited.Kill(entireProcessTree: true); } catch { }
                        procExited.Dispose();
                        _serverProcess = null;
                        _apiBaseUrl = string.Empty;

                        var dataDir = PathHelper.TorrentDataDir;
                        var sessionFile = Path.Combine(dataDir, "session.json");
                        if (File.Exists(sessionFile))
                        {
                            try
                            {
                                var backup = Path.Combine(dataDir, $"session.json.corrupted_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
                                File.Copy(sessionFile, backup, true);
                            }
                            catch { }
                            try
                            {
                                File.WriteAllText(sessionFile, "{\"torrents\":{}}", new UTF8Encoding(false));
                            }
                            catch { }
                        }
                    }

                    continue;
                }

                if (procExited != null && procExited.HasExited)
                {
                    var logs = string.Join(" | ", recentLogs.ToArray().TakeLast(5));
                    throw new InvalidOperationException(
                        $"rqbit process exited prematurely (code {procExited.ExitCode}): {logs}");
                }

                throw new TimeoutException($"Timed out waiting for rqbit to respond at {_apiBaseUrl}");
            }
        }

        private static (HashSet<string> Folders, HashSet<string> GameIds) GetActiveDownloadFoldersAndIds()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var libraryFile = PathHelper.LibraryFile;
                if (!File.Exists(libraryFile))
                    return (folders, gameIds);

                var json = File.ReadAllText(libraryFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return (folders, gameIds);

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var stateStr = el.TryGetProperty("State", out var sProp) ? sProp.GetString() : null;
                    var wasDownloading = el.TryGetProperty("WasDownloadingWhenClosed", out var wProp) && wProp.GetBoolean();
                    var isQueued = el.TryGetProperty("IsQueued", out var qProp) && qProp.GetBoolean();

                    bool isActive = wasDownloading || isQueued
                        || string.Equals(stateStr, "Downloading", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(stateStr, "Paused", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(stateStr, "Verifying", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(stateStr, "Installing", StringComparison.OrdinalIgnoreCase);

                    if (!isActive)
                        continue;

                    var gameId = el.TryGetProperty("GameId", out var gProp) ? gProp.GetString() : null;
                    var installPath = el.TryGetProperty("InstallPath", out var ipProp) ? ipProp.GetString() : null;
                    var targetLib = el.TryGetProperty("TargetLibraryPath", out var tProp) ? tProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(gameId))
                    {
                        gameIds.Add(gameId);
                        var sanitizedId = PathHelper.SanitizeFileName(gameId);
                        gameIds.Add(sanitizedId);

                        if (!string.IsNullOrWhiteSpace(targetLib))
                            folders.Add(Path.GetFullPath(Path.Combine(targetLib, "temp", sanitizedId)));

                        if (!string.IsNullOrWhiteSpace(installPath))
                        {
                            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installPath));
                            if (!string.IsNullOrWhiteSpace(parent))
                                folders.Add(Path.GetFullPath(Path.Combine(parent, "temp", sanitizedId)));
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(installPath))
                        folders.Add(Path.GetFullPath(installPath));
                }
            }
            catch
            {
                // Non-fatal if library.json cannot be read
            }

            return (folders, gameIds);
        }

        internal static void SanitizeSessionFile(string dataDir, ILogger? logger = null)
        {
            try
            {
                var sessionFile = Path.Combine(dataDir, "session.json");
                if (!File.Exists(sessionFile))
                    return;

                var bytes = File.ReadAllBytes(sessionFile);
                if (bytes.Length == 0)
                {
                    logger?.LogWarning("session.json was empty (0 bytes). Resetting to empty dictionary.");
                    File.WriteAllText(sessionFile, "{\"torrents\":{}}", new UTF8Encoding(false));
                    return;
                }

                // Check for UTF-8 BOM (0xEF, 0xBB, 0xBF)
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = hasBom
                    ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                    : Encoding.UTF8.GetString(bytes);

                bool isValid = false;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        isValid = true;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "session.json is malformed/corrupted JSON.");
                }

                if (!isValid)
                {
                    var backupPath = Path.Combine(dataDir, $"session.json.corrupted_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
                    try { File.Copy(sessionFile, backupPath, true); } catch { }
                    logger?.LogWarning("Resetting corrupted session.json (backup: {Backup})", backupPath);
                    File.WriteAllText(sessionFile, "{\"torrents\":{}}", new UTF8Encoding(false));
                }
                else if (hasBom)
                {
                    logger?.LogInformation("Stripped UTF-8 BOM from session.json for rqbit compatibility.");
                    File.WriteAllText(sessionFile, text, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to sanitize session.json");
            }
        }

        private void CleanStaleSessionEntries(string dataDir)
        {
            try
            {
                var sessionFile = Path.Combine(dataDir, "session.json");
                if (!File.Exists(sessionFile))
                    return;

                var sessionJson = File.ReadAllText(sessionFile);
                using var doc = JsonDocument.Parse(sessionJson);
                if (!doc.RootElement.TryGetProperty("torrents", out var torrentsObj) ||
                    torrentsObj.ValueKind != JsonValueKind.Object)
                    return;

                var (activeFolders, activeIds) = GetActiveDownloadFoldersAndIds();
                var remainingTorrents = new Dictionary<string, JsonElement>();
                var staleHashes = new List<string>();
                bool changed = false;

                foreach (var prop in torrentsObj.EnumerateObject())
                {
                    var torrentObj = prop.Value;
                    var outputFolder = torrentObj.TryGetProperty("output_folder", out var ofProp) ? ofProp.GetString() : null;
                    var infoHash = torrentObj.TryGetProperty("info_hash", out var ihProp) ? ihProp.GetString() : null;

                    bool isValid = false;
                    if (!string.IsNullOrWhiteSpace(outputFolder))
                    {
                        var fullOutput = Path.GetFullPath(outputFolder);
                        if (fullOutput.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                        {
                            isValid = true;
                        }
                        else
                        {
                            foreach (var activeFolder in activeFolders)
                            {
                                if (fullOutput.Equals(activeFolder, StringComparison.OrdinalIgnoreCase) ||
                                    fullOutput.StartsWith(activeFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                {
                                    isValid = true;
                                    break;
                                }
                            }

                            if (!isValid)
                            {
                                foreach (var gid in activeIds)
                                {
                                    if (PathHasSegment(outputFolder, gid))
                                    {
                                        isValid = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (isValid)
                    {
                        remainingTorrents[prop.Name] = torrentObj.Clone();
                    }
                    else
                    {
                        changed = true;
                        _logger.LogInformation("Removing stale torrent {Id} ({Output}, hash {Hash}) from session.json",
                            prop.Name, outputFolder, infoHash);
                        if (!string.IsNullOrWhiteSpace(infoHash))
                            staleHashes.Add(infoHash);
                    }
                }

                if (changed)
                {
                    var updatedDict = new Dictionary<string, object>
                    {
                        ["torrents"] = remainingTorrents
                    };
                    var updatedJson = JsonSerializer.Serialize(updatedDict);
                    File.WriteAllText(sessionFile, updatedJson, new UTF8Encoding(false));

                    foreach (var hash in staleHashes)
                    {
                        try
                        {
                            var torrentFile = Path.Combine(dataDir, $"{hash}.torrent");
                            if (File.Exists(torrentFile)) File.Delete(torrentFile);

                            var bitvFile = Path.Combine(dataDir, $"{hash}.bitv");
                            if (File.Exists(bitvFile)) File.Delete(bitvFile);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Could not delete stale torrent metadata file for hash {Hash}", hash);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean stale rqbit session entries");
            }
        }

        private async Task ReconcileRestoredTorrentsAsync(HttpResponseMessage listResponse, CancellationToken ct)
        {
            try
            {
                var body = await listResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("torrents", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return;

                var (activeFolders, activeIds) = GetActiveDownloadFoldersAndIds();
                var trackedIds = new HashSet<int>(_torrentIds.Values);

                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                    {
                        if (trackedIds.Contains(id))
                            continue;

                        var outputFolder = item.TryGetProperty("output_folder", out var ofProp) ? ofProp.GetString() : null;
                        bool isValid = false;

                        if (!string.IsNullOrWhiteSpace(outputFolder))
                        {
                            var fullOutput = Path.GetFullPath(outputFolder);
                            if (fullOutput.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                            {
                                isValid = true;
                            }
                            else
                            {
                                foreach (var activeFolder in activeFolders)
                                {
                                    if (fullOutput.Equals(activeFolder, StringComparison.OrdinalIgnoreCase) ||
                                        fullOutput.StartsWith(activeFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isValid = true;
                                        break;
                                    }
                                }

                                if (!isValid)
                                {
                                    foreach (var gid in activeIds)
                                    {
                                        if (PathHasSegment(outputFolder, gid))
                                        {
                                            isValid = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        if (!isValid)
                        {
                            _logger.LogInformation("Forgetting stale untracked restored torrent id {Id} (output: {Output})", id, outputFolder);
                            await SafeForgetAsync(id).ConfigureAwait(false);
                        }
                        else
                        {
                            await SafePauseAsync(id).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect restored torrents on startup");
            }
        }

        private async Task<bool> TryStartTorrentAsync(int torrentId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_apiBaseUrl))
                return false;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await _http
                    .PostAsync($"{_apiBaseUrl}/torrents/{torrentId}/start", null, timeout.Token)
                    .ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "rqbit start call failed for id {Id}", torrentId);
                return false;
            }
        }

        private async Task<int?> FindTorrentForFolderAsync(string destinationFolder, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_apiBaseUrl))
                return null;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await _http.GetAsync($"{_apiBaseUrl}/torrents", timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("torrents", out var torrents)
                    || torrents.ValueKind != JsonValueKind.Array)
                    return null;

                var expected = Path.GetFullPath(destinationFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var item in torrents.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idProperty) || !idProperty.TryGetInt32(out var id)
                        || !item.TryGetProperty("output_folder", out var folderProperty))
                        continue;

                    var folder = folderProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(folder)
                        && Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Equals(expected, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not match restored rqbit torrent to {Folder}", destinationFolder);
            }

            return null;
        }

        /// <summary>True when the destination folder already holds downloaded data.</summary>
        private static bool HasExistingDownloadData(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return false;
                foreach (var _ in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    return true;
            }
            catch
            {
                // A locked folder must not change how the torrent is added.
            }

            return false;
        }

        /// <summary>
        /// File holding the PID of the sidecar process we launched, so a subsequent
        /// launcher run can identify and reap exactly that orphaned process. Using the
        /// recorded PID instead of scanning every process named rqbit/Nexora.TorrentEngine
        /// prevents us from killing an unrelated engine (for example a seeder that a
        /// test started, or a second torrent client using the same binary).
        /// </summary>
        private static string SidecarPidFile => Path.Combine(PathHelper.TorrentDataDir, "sidecar.pid");

        private static void KillStaleTorrentEngines(string exePath)
        {
            // A previous launcher instance may have crashed and left its sidecar running.
            // Two engines writing the same files make Continue look broken, so reap the
            // exact orphaned process recorded in the PID file before starting a new one.
            try
            {
                var pidFile = SidecarPidFile;
                if (File.Exists(pidFile))
                {
                    if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid > 0)
                    {
                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            // Only touch our own engine binary, never another torrent client.
                            if (string.Equals(process.MainModule?.FileName, exePath,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                process.Kill(entireProcessTree: true);
                                process.WaitForExit(2000);
                            }
                        }
                        catch
                        {
                            // Access denied / already exited — ignore.
                        }
                    }

                    try { File.Delete(pidFile); } catch { /* best effort */ }
                }
            }
            catch
            {
                // The PID file is best-effort; the new sidecar can still start.
            }
        }

        private static string? LocateRqbitExecutable()
        {
            // Explicit override via environment variable takes highest precedence
            var envPath = Environment.GetEnvironmentVariable("GAMEVAULT_RQBIT_PATH");
            if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
                return envPath;

            var candidates = new[]
            {
                // 1. Nexora-labelled engine beside the launcher in published / installed layout
                Path.Combine(AppContext.BaseDirectory, "Nexora.TorrentEngine.exe"),
                // 2. Legacy next-to-app name
                Path.Combine(AppContext.BaseDirectory, "rqbit.exe"),
                // 3. In tools/bin relative to executable
                Path.Combine(AppContext.BaseDirectory, "tools", "bin", "rqbit.exe"),
                // 4. Development workspace layout, wherever it is checked out
                PathHelper.FindFileWalkingUp(Path.Combine("tools", "bin", "rqbit.exe"))
            };

            return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        }

        private static int GetAvailablePort(int preferredPort)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, preferredPort);
                listener.Start();
                listener.Stop();
                return preferredPort;
            }
            catch
            {
                // Fall back to ephemeral loopback port
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        private static int GetAvailablePeerPort(out bool inReservedRange)
        {
            for (var port = PeerPortStart; port <= PeerPortEnd; port++)
            {
                try
                {
                    using var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    using var udpListener = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                    inReservedRange = true;
                    return port;
                }
                catch
                {
                    // Another client is using this port. Try the next port in Nexora's firewall range.
                }
            }

            // If the reserved firewall range is busy, choose a free TCP+UDP port. UPnP may
            // still forward it; outgoing peer connections remain available regardless.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                using var tcpProbe = new TcpListener(IPAddress.Any, 0);
                tcpProbe.Start();
                var candidate = ((IPEndPoint)tcpProbe.LocalEndpoint).Port;
                tcpProbe.Stop();
                try
                {
                    using var udpProbe = new UdpClient(new IPEndPoint(IPAddress.Any, candidate));
                    inReservedRange = false;
                    return candidate;
                }
                catch
                {
                    // Retry if another process claimed the random port between probes.
                }
            }

            inReservedRange = false;
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        // ─────────────────────────────────────────────────────────────────
        // ITorrentDownloader Implementation
        // ─────────────────────────────────────────────────────────────────

        public async Task<OperationResult> DownloadAsync(
            string gameId,
            string magnetOrTorrentUrl,
            bool isTorrentFileUrl,
            string destinationFolder,
            IProgress<DownloadProgress>? progress = null,
            long maxDownloadSpeedBps = 0,
            long maxUploadSpeedBps = 0,
            CancellationToken ct = default,
            IEnumerable<string>? additionalTrackers = null)
        {
            var priorityAcquired = false;
            int? torrentId = null;

            // A new download/resume attempt clears any previous deliberate pause so the
            // "resume if paused" nudge below is allowed to act.
            _pauseRequested.TryRemove(gameId, out _);

            try
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report(new DownloadProgress
                {
                    GameId = gameId,
                    IsTorrent = true,
                    Phase = Localizer.T("TorrentPhaseStarting", "Запуск торрент-клиента…"),
                    PhaseKey = DownloadPhases.Starting
                });

                Directory.CreateDirectory(destinationFolder);

                // When seeding is disabled, cap upload to 1 B/s so rqbit effectively
                // stops uploading pieces to the swarm while still downloading.
                var effectiveUploadBps = maxUploadSpeedBps;
                if (_settings?.Settings.AllowSeeding != true)
                    effectiveUploadBps = 1;

                await EnsureServerRunningAsync(maxDownloadSpeedBps, effectiveUploadBps, ct).ConfigureAwait(false);

                // The sidecar is alive now: raise its priority (not the launcher's) so the
                // UI stays responsive while the torrent engine gets the CPU it needs.
                AcquirePriority();
                priorityAcquired = true;

                // rqbit restores its persisted session after process restart, while
                // this in-memory mapping is empty. Rebind by output folder before
                // adding anything so Continue reuses the existing torrent/bitfield.
                if (!_torrentIds.ContainsKey(gameId))
                {
                    var restoredId = await FindTorrentForFolderAsync(destinationFolder, ct).ConfigureAwait(false);
                    if (restoredId.HasValue)
                    {
                        _torrentIds.TryAdd(gameId, restoredId.Value);
                        _logger.LogInformation("rqbit: rebound restored torrent for {GameId} (Id={TorrentId})",
                            gameId, restoredId.Value);
                    }
                }

                progress?.Report(new DownloadProgress
                {
                    GameId = gameId,
                    IsTorrent = true,
                    Phase = Localizer.T("TorrentPhaseFetchingMetadata", "Получение метаданных торрента…"),
                    PhaseKey = DownloadPhases.FetchingMetadata
                });

                // A paused torrent is intentionally kept inside the engine. Reusing it
                // preserves the already verified pieces and the fast-resume bitfield, so
                // "Continue" really continues instead of restarting a full re-check.
                if (_torrentIds.TryGetValue(gameId, out var knownTorrentId))
                {
                    if (await TryStartTorrentAsync(knownTorrentId, ct).ConfigureAwait(false))
                    {
                        torrentId = knownTorrentId;
                        _logger.LogInformation(
                            "rqbit: reusing paused torrent for {GameId} (Id={TorrentId})", gameId, knownTorrentId);
                    }
                    else
                    {
                        // The process may have restored the session with a different
                        // id after a restart. Re-discover by destination once, while
                        // still never creating a duplicate task.
                        var reboundId = await FindTorrentForFolderAsync(destinationFolder, ct).ConfigureAwait(false);
                        if (reboundId.HasValue && reboundId.Value != knownTorrentId
                            && await TryStartTorrentAsync(reboundId.Value, ct).ConfigureAwait(false))
                        {
                            _torrentIds[gameId] = reboundId.Value;
                            torrentId = reboundId.Value;
                        }
                        else
                        {
                            return OperationResult.Fail(Localizer.T("TorrentErrorInvalidStatus",
                                "Торрент-клиент пока не смог возобновить задачу. Данные сохранены — попробуйте ещё раз."));
                        }
                    }
                }

                if (!torrentId.HasValue)
                {
                    // ── Add torrent ───────────────────────────────────────
                    // rqbit's "overwrite" flag controls whether existing files may be
                    // opened for writing: overwrite=false uses create_new and FAILS if
                    // the file already exists, while overwrite=true re-opens existing
                    // files without truncating (which is exactly what a resume needs).
                    // So a folder that already holds download data MUST use overwrite=true,
                    // otherwise a resume after a launcher restart fails with
                    // "error creating a new file (because allow_overwrite = false)".
                    var overwrite = HasExistingDownloadData(destinationFolder) ? "true" : "false";
                    var addUrl = $"{_apiBaseUrl}/torrents?overwrite={overwrite}&output_folder={Uri.EscapeDataString(destinationFolder)}";

                    HttpResponseMessage addResp;
                    if (isTorrentFileUrl)
                    {
                        var torrentBytes = await FetchTorrentBytesAsync(magnetOrTorrentUrl, ct).ConfigureAwait(false);
                        using var byteContent = new ByteArrayContent(torrentBytes);
                        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                        addResp = await _http.PostAsync(addUrl, byteContent, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var enrichedMagnet = EnrichMagnetWithTrackers(magnetOrTorrentUrl, additionalTrackers);
                        using var stringContent = new StringContent(enrichedMagnet, Encoding.UTF8, "text/plain");
                        addResp = await _http.PostAsync(addUrl, stringContent, ct).ConfigureAwait(false);
                    }

                    var responseBody = await addResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!addResp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("rqbit add torrent failed: {Status} {Body}", addResp.StatusCode, responseBody);
                        return OperationResult.Fail($"Failed to add torrent: {responseBody}");
                    }

                    var addResult = JsonSerializer.Deserialize<RqbitAddTorrentResponse>(responseBody);
                    if (addResult == null)
                        return OperationResult.Fail("Invalid response from rqbit when adding torrent.");

                    torrentId = addResult.Id;
                    _torrentIds[gameId] = torrentId.Value;
                    _logger.LogInformation("rqbit: torrent added for {GameId} (Id={TorrentId})", gameId, torrentId);

                    // Resume in case it was paused
                    await TryStartTorrentAsync(torrentId.Value, ct).ConfigureAwait(false);
                }

                progress?.Report(new DownloadProgress
                {
                    GameId = gameId,
                    IsTorrent = true,
                    Phase = Localizer.T("TorrentPhaseSearchingPeers", "Поиск пиров через DHT и трекеры…"),
                    PhaseKey = DownloadPhases.SearchingPeers
                });

                // ── Progress Polling Loop ──────────────────────────────────
                // A magnet must first find a metadata peer. Public swarms can take several
                // minutes to reconnect, so a short timeout incorrectly rejects healthy torrents.
                var metadataTimeout = TimeSpan.FromMinutes(10);
                var metadataDeadline = DateTime.UtcNow + metadataTimeout;
                var metadataReceived = isTorrentFileUrl; // file already has metadata

                const int stallTimeoutSec = 1800;
                const int maxConsecutiveStatsFailures = 60;
                const int maxErrorRetries = 5;
                int errorRetryCount = 0;
                DateTime? stallSince = null;
                DateTime lastStartAttempt = DateTime.MinValue;
                long lastTotalDownload = 0;
                long smoothedSpeedBps = 0;
                int consecutiveStatsFailures = 0;
                bool torrentFinished = false;
                DateTime lastStallRestart = DateTime.MinValue;
                DateTime lastMetadataRestart = DateTime.MinValue;

                // Poll fast while data or metadata is flowing; back off when the swarm
                // is idle so several paused/starved torrents do not hit the local API
                // dozens of times per second.
                var pollDelayMs = PollIntervalMs;

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(pollDelayMs, ct).ConfigureAwait(false);

                    RqbitStatsResponse? stats = null;
                    try
                    {
                        using var pollTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        pollTimeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                        var statsUrl = $"{_apiBaseUrl}/torrents/{torrentId}/stats/v1";
                        using var statsResp = await _http.GetAsync(statsUrl, pollTimeoutCts.Token).ConfigureAwait(false);
                        if (!statsResp.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("rqbit stats request failed for {GameId}: {Status}", gameId, statsResp.StatusCode);
                            var isEngineAlive = _serverProcess is { HasExited: false };
                            var threshold = isEngineAlive ? maxConsecutiveStatsFailures : 10;
                            if (++consecutiveStatsFailures >= threshold)
                            {
                                await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                                return OperationResult.Fail(Localizer.T("TorrentErrorStateUnavailable", "Не удаётся получить состояние торрента. Частично скачанные данные сохранены; попробуйте возобновить загрузку."));
                            }
                            continue;
                        }

                        var statsJson = await statsResp.Content.ReadAsStringAsync(pollTimeoutCts.Token).ConfigureAwait(false);
                        stats = JsonSerializer.Deserialize<RqbitStatsResponse>(statsJson);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "rqbit stats poll exception for {GameId}", gameId);
                        var isEngineAlive = _serverProcess is { HasExited: false };
                        var threshold = isEngineAlive ? maxConsecutiveStatsFailures : 10;
                        if (++consecutiveStatsFailures >= threshold)
                        {
                            await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                            return OperationResult.Fail(Localizer.T("TorrentErrorStateUnavailable", "Не удаётся получить состояние торрента. Частично скачанные данные сохранены; попробуйте возобновить загрузку."));
                        }
                        continue;
                    }

                    if (stats == null)
                    {
                        var isEngineAlive = _serverProcess is { HasExited: false };
                        var threshold = isEngineAlive ? maxConsecutiveStatsFailures : 10;
                        if (++consecutiveStatsFailures >= threshold)
                        {
                            await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                            return OperationResult.Fail(Localizer.T("TorrentErrorInvalidStatus", "Торрент-клиент возвращает некорректный статус. Частично скачанные данные сохранены."));
                        }
                        continue;
                    }

                    consecutiveStatsFailures = 0;

                    var state = stats.State ?? "unknown";
                    var pauseRequested = _pauseRequested.ContainsKey(gameId);

                    // rqbit restores torrents from its persisted session, and a torrent
                    // that was paused when the launcher closed comes back paused. If its
                    // startup hash check is still running when our /start arrives, rqbit
                    // only clears the pause request; when the check finishes it drops back
                    // into "paused", so Continue looks like it instantly paused itself.
                    // Keep nudging it until it is actually live, unless the user paused.
                    if (!pauseRequested
                        && string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase)
                        && (DateTime.UtcNow - lastStartAttempt).TotalSeconds >= 2)
                    {
                        lastStartAttempt = DateTime.UtcNow;
                        _logger.LogInformation(
                            "rqbit: torrent {TorrentId} is paused during an active download; requesting start",
                            torrentId);
                        await TryStartTorrentAsync(torrentId!.Value, ct).ConfigureAwait(false);
                    }

                    // Always prefer stats.ProgressBytes (total verified bytes on disk,
                    // cumulative across sessions).  stats.Live.Snapshot.DownloadedBytes
                    // is session-only: it resets to 0 on every /start call, so using
                    // it directly causes the UI to jump back to 0 % when the user
                    // presses Continue on a paused download.  Take the max to handle
                    // the unlikely edge case where ProgressBytes has not yet been
                    // committed but the live counter is ahead.
                    var progressBytes = Math.Max(stats.ProgressBytes, stats.Live?.Snapshot?.DownloadedBytes ?? 0);
                    var totalBytes = stats.TotalBytes;
                    var fraction = totalBytes > 0
                        ? Math.Clamp((double)progressBytes / totalBytes, 0.0, 1.0)
                        : 0.0;

                    if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(stats.Error))
                    {
                        var error = stats.Error ?? "unknown torrent engine error";
                        _logger.LogWarning("rqbit reported an error for {GameId}: {Error}", gameId, error);

                        // Try to auto-recover transient I/O, lock or network errors before failing
                        if (++errorRetryCount <= maxErrorRetries)
                        {
                            _logger.LogInformation("Attempting auto-recovery for {GameId} (attempt {Attempt}/{Max})",
                                gameId, errorRetryCount, maxErrorRetries);
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                            await TryStartTorrentAsync(torrentId!.Value, ct).ConfigureAwait(false);
                            continue;
                        }

                        // Keep torrent paused rather than forgetting it so data and hash checks are preserved
                        await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                        return OperationResult.Fail(string.Format(Localizer.T("TorrentErrorClientFormat", "Торрент-клиент сообщил об ошибке: {0}. Частично скачанные данные сохранены."), error));
                    }
                    else
                    {
                        errorRetryCount = 0;
                    }

                    // peer_stats.live = actively uploading peers
                    // live_peers.live = same but from the live_peers sub-object
                    // Use peer_stats (more complete) when available, fall back to live_peers.
                    var livePeers = stats.Live?.Snapshot?.PeerStats?.Live
                                    ?? stats.Live?.Snapshot?.LivePeers?.Live
                                    ?? 0;
                    var connectingPeers = stats.Live?.Snapshot?.PeerStats?.Connecting
                                         ?? stats.Live?.Snapshot?.LivePeers?.Connecting
                                         ?? 0;
                    var seenPeers = stats.Live?.Snapshot?.PeerStats?.Seen ?? 0;
                    var dlSpeedMbps = stats.Live?.DownloadSpeed?.Mbps ?? 0.0;
                    var upSpeedMbps = stats.Live?.UploadSpeed?.Mbps ?? 0.0;

                    var dlRateBps = (long)(dlSpeedMbps * 1024 * 1024);
                    var upRateBps = (long)(upSpeedMbps * 1024 * 1024);

                    if (dlRateBps > 0)
                        smoothedSpeedBps = smoothedSpeedBps == 0 ? dlRateBps : (long)(smoothedSpeedBps * 0.7 + dlRateBps * 0.3);
                    else
                        smoothedSpeedBps = (long)(smoothedSpeedBps * 0.5);

                    // Check metadata
                    if (!metadataReceived)
                    {
                        if (totalBytes > 0)
                        {
                            metadataReceived = true;
                        }
                        else
                        {
                            // Periodically cycle torrent to refresh DHT/tracker metadata queries
                            var metadataWaitSec = metadataTimeout.TotalSeconds - (metadataDeadline - DateTime.UtcNow).TotalSeconds;
                            if (metadataWaitSec >= 120 && (DateTime.UtcNow - lastMetadataRestart).TotalSeconds >= 120)
                            {
                                lastMetadataRestart = DateTime.UtcNow;
                                _logger.LogInformation(
                                    "rqbit: cycling torrent {TorrentId} to retry metadata discovery ({Sec}s elapsed)",
                                    torrentId, (int)metadataWaitSec);
                                await SafePauseAsync(torrentId!.Value).ConfigureAwait(false);
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                await TryStartTorrentAsync(torrentId!.Value, ct).ConfigureAwait(false);
                            }

                            if (DateTime.UtcNow > metadataDeadline)
                            {
                                _logger.LogWarning("rqbit: metadata timeout for {GameId} after {Sec}s", gameId, metadataTimeout.TotalSeconds);
                                await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                                return OperationResult.Fail(Localizer.T("TorrentErrorNoMetadata", "Не удалось получить метаданные торрента — нет доступных пиров. Попробуйте позже."));
                            }
                        }
                    }

                    // Stall detection
                    bool isStalled = false;
                    if (metadataReceived && totalBytes > 0 && progressBytes < totalBytes)
                    {
                        if (progressBytes > lastTotalDownload || dlRateBps > 0)
                        {
                            lastTotalDownload = Math.Max(lastTotalDownload, progressBytes);
                            stallSince = null;
                        }
                        else
                        {
                            stallSince ??= DateTime.UtcNow;
                            var stallElapsedSec = (DateTime.UtcNow - stallSince.Value).TotalSeconds;
                            isStalled = stallElapsedSec >= 20 && livePeers == 0;
                            if (stallElapsedSec > stallTimeoutSec)
                            {
                                _logger.LogWarning("rqbit: download stalled for {GameId} — no progress for {Sec}s", gameId, stallTimeoutSec);
                                await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                                return OperationResult.Fail(Localizer.T("TorrentErrorNoPeers", "Загрузка не идёт — нет активных пиров. Проверьте подключение или попробуйте позже."));
                            }

                            // Active stall recovery: cycle torrent to force tracker re-announce and DHT re-query
                            if (isStalled && stallElapsedSec >= 90
                                && (DateTime.UtcNow - lastStallRestart).TotalSeconds >= 120)
                            {
                                lastStallRestart = DateTime.UtcNow;
                                _logger.LogInformation(
                                    "rqbit: cycling torrent {TorrentId} to recover from stall ({Sec}s with no peers)",
                                    torrentId, (int)stallElapsedSec);
                                await SafePauseAsync(torrentId!.Value).ConfigureAwait(false);
                                await Task.Delay(1000, ct).ConfigureAwait(false);
                                await TryStartTorrentAsync(torrentId!.Value, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        stallSince = null;
                    }

                    // ETA calculation using smoothed speed
                    long secsRemaining = -1;
                    var etaSpeed = smoothedSpeedBps >= 1000 ? smoothedSpeedBps : dlRateBps;
                    if (etaSpeed >= 1000 && fraction < 1.0 && totalBytes > progressBytes)
                    {
                        secsRemaining = (long)((totalBytes - progressBytes) / (double)etaSpeed);
                    }

                    var (phaseText, phaseKey) = GetPhase(state, livePeers, totalBytes > 0, pauseRequested);

                    progress?.Report(new DownloadProgress
                    {
                        GameId = gameId,
                        IsTorrent = true,
                        Fraction = fraction,
                        BytesDownloaded = progressBytes,
                        TotalBytes = totalBytes > 0 ? totalBytes : -1,
                        SpeedBytesPerSec = smoothedSpeedBps > 0 ? smoothedSpeedBps : dlRateBps,
                        UploadSpeedBytesPerSec = upRateBps,
                        SecondsRemaining = secsRemaining,
                        Peers = livePeers,
                        TotalPeers = seenPeers > 0 ? seenPeers : livePeers + connectingPeers,
                        IsStalled = isStalled,
                        Phase = phaseText,
                        PhaseKey = phaseKey,
                        CurrentFile = gameId
                    });

                    // Finished?
                    if (stats.Finished || string.Equals(state, "finished", StringComparison.OrdinalIgnoreCase))
                    {
                        torrentFinished = true;
                        break;
                    }

                    pollDelayMs = dlRateBps > 0 || !metadataReceived
                        ? PollIntervalMs
                        : SlowPollIntervalMs;
                }

                // Cancellation?
                if (ct.IsCancellationRequested)
                {
                    // Keep the torrent paused in the engine instead of forgetting it:
                    // forgetting drops fast-resume data and forces a fresh metadata
                    // lookup when the user presses Continue.
                    if (torrentId.HasValue)
                        await SafePauseAsync(torrentId.Value).ConfigureAwait(false);

                    return OperationResult.Fail("Download paused/cancelled.");
                }

                if (!torrentFinished)
                {
                    if (torrentId.HasValue)
                        await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                    return OperationResult.Fail(Localizer.T("TorrentErrorIncompleteVerify", "Торрент завершился без подтверждения полной проверки данных."));
                }

                // Success
                // With seeding enabled, leave the torrent alive so it keeps uploading to
                // the swarm while the game is verified/installed. The GameManager stops
                // the seeding (and forgets the torrent) once the install is finished and
                // the staging folder is removed.
                var keepSeeding = _settings?.Settings.AllowSeeding == true;
                if (torrentId.HasValue && !keepSeeding)
                {
                    await SafeForgetAsync(torrentId.Value).ConfigureAwait(false);
                }
                if (!keepSeeding)
                    _torrentIds.TryRemove(gameId, out _);

                progress?.Report(new DownloadProgress
                {
                    GameId = gameId,
                    IsTorrent = true,
                    Fraction = 1.0,
                    Phase = "Complete",
                    PhaseKey = DownloadPhases.Complete
                });

                return OperationResult.Ok();
            }
            catch (OperationCanceledException)
            {
                if (torrentId.HasValue)
                    await SafePauseAsync(torrentId.Value).ConfigureAwait(false);

                return OperationResult.Fail("Download paused/cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "rqbit: download failed for {GameId}", gameId);
                if (torrentId.HasValue)
                    await SafePauseAsync(torrentId.Value).ConfigureAwait(false);
                return OperationResult.Fail($"Torrent error: {ex.Message}", ex);
            }
            finally
            {
                if (priorityAcquired)
                    ReleasePriority();
            }
        }

        public async Task PauseAsync(string gameId)
        {
            _pauseRequested[gameId] = 0;
            if (_torrentIds.TryGetValue(gameId, out var id))
                await SafePauseAsync(id).ConfigureAwait(false);
        }

        public async Task StopAsync(string gameId)
        {
            await ForgetTorrentForGameAsync(gameId).ConfigureAwait(false);
        }

        public async Task RemoveAsync(string gameId)
        {
            await ForgetTorrentForGameAsync(gameId).ConfigureAwait(false);
        }

        private async Task ForgetTorrentForGameAsync(string gameId)
        {
            if (_torrentIds.TryRemove(gameId, out var id))
            {
                await SafeForgetAsync(id).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(_apiBaseUrl))
                return;

            // Exact folders recorded in library.json for this game. Never match by a
            // substring: "doom" must not forget the torrent of "doom-eternal".
            var expectedFolders = GetFoldersForGame(gameId);
            var sanitizedId = PathHelper.SanitizeFileName(gameId);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var resp = await _http.GetAsync($"{_apiBaseUrl}/torrents", cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return;

                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("torrents", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var torrentId))
                        {
                            var outputFolder = item.TryGetProperty("output_folder", out var ofProp) ? ofProp.GetString() : null;
                            if (string.IsNullOrWhiteSpace(outputFolder))
                                continue;

                            var fullOutput = Path.GetFullPath(outputFolder);
                            var matches = expectedFolders.Any(folder =>
                                    string.Equals(fullOutput, folder, StringComparison.OrdinalIgnoreCase))
                                // Fallback for entries missing from library.json: the
                                // folder name must equal the id, not merely contain it.
                                || PathHasSegment(fullOutput, sanitizedId);

                            if (matches)
                                await SafeForgetAsync(torrentId).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query rqbit torrents while forgetting game {GameId}", gameId);
            }
        }

        /// <summary>
        /// Download/install folders recorded for a game in library.json. Used to match
        /// restored torrents exactly after a restart.
        /// </summary>
        private static List<string> GetFoldersForGame(string gameId)
        {
            var result = new List<string>();
            try
            {
                var libraryFile = PathHelper.LibraryFile;
                if (!File.Exists(libraryFile))
                    return result;

                using var doc = JsonDocument.Parse(File.ReadAllText(libraryFile));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return result;

                var sanitized = PathHelper.SanitizeFileName(gameId);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var id = el.TryGetProperty("GameId", out var idProp) ? idProp.GetString() : null;
                    if (!string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var installPath = el.TryGetProperty("InstallPath", out var ip) ? ip.GetString() : null;
                    var targetLibrary = el.TryGetProperty("TargetLibraryPath", out var tp) ? tp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(installPath))
                    {
                        var fullInstall = Path.GetFullPath(installPath);
                        result.Add(fullInstall);

                        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullInstall));
                        if (!string.IsNullOrWhiteSpace(parent))
                            result.Add(Path.GetFullPath(Path.Combine(parent, "temp", sanitized)));
                    }

                    if (!string.IsNullOrWhiteSpace(targetLibrary))
                        result.Add(Path.GetFullPath(Path.Combine(targetLibrary, "temp", sanitized)));
                }
            }
            catch
            {
                // A locked/corrupt library file only disables the exact-match path.
            }

            return result;
        }

        /// <summary>True when one of the path segments equals <paramref name="segment"/>.</summary>
        internal static bool PathHasSegment(string path, string segment)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment))
                return false;

            try
            {
                var parts = Path.GetFullPath(path).Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                return parts.Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        public async Task PauseAndSaveResumeAsync(string gameId)
        {
            _pauseRequested[gameId] = 0;
            if (_torrentIds.TryGetValue(gameId, out var id))
            {
                await SafePauseAsync(id).ConfigureAwait(false);
                // Give librqbit a moment to flush its fast-resume bitfield before the
                // sidecar process is killed on application exit.
                await Task.Delay(300).ConfigureAwait(false);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────

        private async Task SafePauseAsync(int id)
        {
            if (string.IsNullOrEmpty(_apiBaseUrl)) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _http.PostAsync($"{_apiBaseUrl}/torrents/{id}/pause", null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "rqbit pause failed for id {Id}", id);
            }
        }

        private async Task SafeForgetAsync(int id)
        {
            if (string.IsNullOrEmpty(_apiBaseUrl)) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                // /torrents/{id}/forget keeps downloaded files on disk
                await _http.PostAsync($"{_apiBaseUrl}/torrents/{id}/forget", null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "rqbit forget failed for id {Id}", id);
            }
        }

        private static (string Text, string Key) GetPhase(
            string state, int peers, bool hasMetadata, bool isPauseRequested = false)
        {
            if (!hasMetadata)
                return (Localizer.T("TorrentPhaseFetchingMetadata", "Получение метаданных торрента…"),
                    DownloadPhases.FetchingMetadata);

            return state switch
            {
                "initializing" => (Localizer.T("TorrentPhaseCheckingIntegrity", "Проверка целостности данных…"),
                    DownloadPhases.CheckingIntegrity),
                "paused" => isPauseRequested
                    ? (Localizer.T("TorrentPhasePaused", "Пауза"), DownloadPhases.Paused)
                    : (Localizer.T("TorrentPhaseSearchingPeersShort", "Поиск пиров…"), DownloadPhases.SearchingPeers),
                "finished" => ("Complete", DownloadPhases.Complete),
                "live" => peers == 0
                    ? (Localizer.T("TorrentPhaseSearchingPeersShort", "Поиск пиров…"), DownloadPhases.SearchingPeers)
                    : (Localizer.T("TorrentPhaseDownloading", "Загрузка…"), DownloadPhases.Downloading),
                _ => peers == 0
                    ? (Localizer.T("TorrentPhaseSearchingPeers", "Поиск пиров через DHT и трекеры…"), DownloadPhases.SearchingPeers)
                    : (Localizer.T("TorrentPhaseDownloading", "Загрузка…"), DownloadPhases.Downloading)
            };
        }

        private static readonly string[] _publicTrackers =
        [
            // Tier 1: Highest-reliability public trackers
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.demonii.com:1337/announce",
            "udp://tracker.openbittorrent.com:80/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://explodie.org:6969/announce",
            "udp://exodus.desync.com:6969/announce",
            "udp://tracker.dler.com:6969/announce",
            // Tier 2: Additional well-known trackers
            "udp://opentor.net:6969",
            "udp://tracker.moeking.me:6969/announce",
            "udp://opentracker.i2p.rocks:6969/announce",
            "udp://tracker.tiny-vps.com:6969/announce",
            "udp://p4p.arenabg.com:1337/announce",
            "udp://tracker.birkenwald.de:6969/announce",
            "udp://tracker.pomf.se:80/announce",
            "udp://tracker.theoks.net:6969/announce",
            "udp://tracker-udp.gbitt.info:80/announce",
            "udp://movies.zsw.ca:6969/announce",
            // HTTP/HTTPS fallbacks for restrictive networks that block UDP
            "http://tracker.opentrackr.org:1337/announce",
            "http://tracker.openbittorrent.com:80/announce",
            "https://tracker.gbitt.info:443/announce",
            "https://tracker.lilithraws.org:443/announce",
        ];

        private static string EnrichMagnetWithTrackers(string magnet, IEnumerable<string>? additional)
        {
            var sb = new StringBuilder(magnet);
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(magnet, @"[?&]tr=([^&]+)"))
            {
                existing.Add(Uri.UnescapeDataString(m.Groups[1].Value));
            }

            void Append(string tracker)
            {
                if (existing.Add(tracker))
                    sb.Append("&tr=").Append(Uri.EscapeDataString(tracker));
            }

            if (additional != null)
                foreach (var t in additional) Append(t);

            foreach (var t in _publicTrackers)
                Append(t);

            return sb.ToString();
        }

        private async Task<byte[]> FetchTorrentBytesAsync(string url, CancellationToken ct)
        {
            if (File.Exists(url))
                return await File.ReadAllBytesAsync(url, ct).ConfigureAwait(false);

            if (Uri.TryCreate(url, UriKind.Absolute, out var localUri) &&
                localUri.IsFile && File.Exists(localUri.LocalPath))
                return await File.ReadAllBytesAsync(localUri.LocalPath, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var bytes = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);

            if (bytes.Length == 0 || bytes[0] != (byte)'d')
            {
                var idMatch = System.Text.RegularExpressions.Regex.Match(url, @"/download/(\d+)");
                if (idMatch.Success)
                {
                    var tid = idMatch.Groups[1].Value;
                    foreach (var alt in new[]
                    {
                        $"http://d.rutor.info/download/{tid}",
                        $"https://mirror-rutor.org/download/{tid}",
                        $"https://rutor.is/download/{tid}"
                    })
                    {
                        if (string.Equals(alt, url, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using var altReq = new HttpRequestMessage(HttpMethod.Get, alt);
                            using var altResp = await _http.SendAsync(altReq, timeoutCts.Token).ConfigureAwait(false);
                            if (!altResp.IsSuccessStatusCode) continue;
                            var altBytes = await altResp.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
                            if (altBytes.Length > 0 && altBytes[0] == (byte)'d')
                            {
                                _logger.LogInformation("rqbit: fetched .torrent from mirror {Alt}", alt);
                                return altBytes;
                            }
                        }
                        catch { /* try next */ }
                    }
                }
                throw new InvalidDataException($"Response from {url} is not a valid BEncoded torrent file.");
            }

            return bytes;
        }

        private void AcquirePriority()
        {
            lock (_priorityLock)
            {
                _activeDownloads++;
                if (_activeDownloads > 1 || _priorityRaised)
                    return;

                var process = _serverProcess;
                if (process == null || process.HasExited)
                    return;

                try
                {
                    _prevPriority = process.PriorityClass;
                    process.PriorityClass = ProcessPriorityClass.AboveNormal;
                    _priorityProcess = process;
                    _priorityRaised = true;
                    _logger.LogInformation("rqbit: sidecar priority raised to {Priority}", process.PriorityClass);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "rqbit: could not raise the sidecar priority");
                }
            }
        }

        private void ReleasePriority()
        {
            lock (_priorityLock)
            {
                if (_activeDownloads == 0)
                    return;

                _activeDownloads--;
                if (_activeDownloads > 0 || !_priorityRaised)
                    return;

                var process = _priorityProcess;
                _priorityProcess = null;
                try
                {
                    if (process != null && !process.HasExited)
                        process.PriorityClass = _prevPriority;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "rqbit: could not restore the sidecar priority");
                }
                finally
                {
                    _priorityRaised = false;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_processLock)
            {
                if (_serverProcess != null && !_serverProcess.HasExited)
                {
                    try
                    {
                        _logger.LogInformation("Stopping rqbit sidecar process (pid={Pid})", _serverProcess.Id);
                        _serverProcess.Kill(entireProcessTree: true);
                        _serverProcess.WaitForExit(3000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error stopping rqbit process on disposal");
                    }
                    finally
                    {
                        _serverProcess.Dispose();
                        _serverProcess = null;
                    }

                    // The sidecar is gone, so the recorded PID is no longer valid.
                    try { File.Delete(SidecarPidFile); } catch { /* best effort */ }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // JSON Models for rqbit HTTP API v9.x
        //
        // Verified against live rqbit 9.0.1 responses:
        //   POST /torrents  => { "id": N, "details": { "id", "info_hash", "name", "total_pieces" } }
        //   GET  /torrents/{id}/stats  => see RqbitStatsResponse below
        // ─────────────────────────────────────────────────────────────────

        private sealed class RqbitAddTorrentResponse
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("details")]
            public RqbitTorrentDetails? Details { get; set; }
        }

        private sealed class RqbitTorrentDetails
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("info_hash")]
            public string? InfoHash { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("total_pieces")]
            public int TotalPieces { get; set; }
        }

        // Actual rqbit 9.x stats response shape (verified):
        // {
        //   "state": "initializing|live|paused|finished|error",
        //   "error": null | "string",
        //   "progress_bytes": 0,
        //   "total_bytes": 0,
        //   "finished": false,
        //   "live": {
        //     "snapshot": {
        //       "downloaded_and_checked_bytes": 0,
        //       "peer_stats": { "seen":0, "connecting":0, "live":0, ... },
        //       "live_peers": { "connecting":0, "live":0, "dead":0 }
        //     },
        //     "download_speed": { "mbps": 0.0, "human_readable": "0 B/s" },
        //     "upload_speed":   { "mbps": 0.0, "human_readable": "0 B/s" }
        //   }
        // }
        private sealed class RqbitStatsResponse
        {
            [JsonPropertyName("state")]
            public string? State { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("progress_bytes")]
            public long ProgressBytes { get; set; }

            [JsonPropertyName("total_bytes")]
            public long TotalBytes { get; set; }

            [JsonPropertyName("finished")]
            public bool Finished { get; set; }

            [JsonPropertyName("live")]
            public RqbitLiveStats? Live { get; set; }
        }

        private sealed class RqbitLiveStats
        {
            [JsonPropertyName("snapshot")]
            public RqbitSnapshot? Snapshot { get; set; }

            [JsonPropertyName("download_speed")]
            public RqbitSpeed? DownloadSpeed { get; set; }

            [JsonPropertyName("upload_speed")]
            public RqbitSpeed? UploadSpeed { get; set; }
        }

        private sealed class RqbitSnapshot
        {
            /// <summary>Bytes downloaded and hash-verified.</summary>
            [JsonPropertyName("downloaded_and_checked_bytes")]
            public long? DownloadedBytes { get; set; }

            /// <summary>
            /// peer_stats: aggregate counts (seen, connecting, live, dead, queued).
            /// This is the correct field for total-peers telemetry.
            /// </summary>
            [JsonPropertyName("peer_stats")]
            public RqbitPeerStats? PeerStats { get; set; }

            /// <summary>
            /// live_peers: only currently-active peer counts (live, connecting, dead).
            /// </summary>
            [JsonPropertyName("live_peers")]
            public RqbitLivePeers? LivePeers { get; set; }
        }

        private sealed class RqbitLivePeers
        {
            [JsonPropertyName("live")]
            public int Live { get; set; }

            [JsonPropertyName("connecting")]
            public int Connecting { get; set; }

            [JsonPropertyName("dead")]
            public int Dead { get; set; }
        }

        private sealed class RqbitPeerStats
        {
            /// <summary>Peers ever seen (cumulative).</summary>
            [JsonPropertyName("seen")]
            public int Seen { get; set; }

            [JsonPropertyName("connecting")]
            public int Connecting { get; set; }

            /// <summary>Currently live (uploading to us).</summary>
            [JsonPropertyName("live")]
            public int Live { get; set; }

            [JsonPropertyName("dead")]
            public int Dead { get; set; }

            [JsonPropertyName("queued")]
            public int Queued { get; set; }
        }

        private sealed class RqbitSpeed
        {
            [JsonPropertyName("mbps")]
            public double Mbps { get; set; }
        }
    }
}
