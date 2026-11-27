using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Tests
{
    /// <summary>
    /// Tiny HTTP/1.1 file server for update tests. Uses TcpListener so it never needs
    /// a URL ACL and never touches the real network.
    /// </summary>
    internal sealed class LocalUpdateServer : IAsyncDisposable, IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string?> _signatures = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string?> _keyIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _failFirst = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (int Chunk, int DelayMs)> _slow = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _truncateAt = new(StringComparer.OrdinalIgnoreCase);

        public int Port { get; }
        public string BaseUrl { get; }
        public ConcurrentQueue<(string Path, string? Range)> Requests { get; } = new();

        public LocalUpdateServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{Port}/";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Url(string path) => BaseUrl + path.TrimStart('/');

        public void Put(string path, byte[] data, string? signature = null, string? keyId = null)
        {
            path = path.TrimStart('/');
            _files[path] = data;
            _signatures[path] = signature;
            _keyIds[path] = keyId;
        }

        public void FailFirst(string path, int count) => _failFirst[path.TrimStart('/')] = count;

        public void Slow(string path, int chunkSize = 32 * 1024, int delayMs = 80)
            => _slow[path.TrimStart('/')] = (chunkSize, delayMs);

        public void Truncate(string path, int bytes)
            => _truncateAt[path.TrimStart('/')] = Math.Max(0, bytes);

        public int CountRequests(string path)
        {
            var count = 0;
            foreach (var request in Requests)
            {
                if (string.Equals(request.Path, path.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        public string? LastRange(string path)
        {
            string? found = null;
            foreach (var request in Requests)
            {
                if (string.Equals(request.Path, path.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
                    found = request.Range;
            }
            return found;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(requestLine))
                        return;

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(stream, 405, "Method Not Allowed", Array.Empty<byte>(), null).ConfigureAwait(false);
                        return;
                    }

                    var target = parts[1];
                    var query = target.IndexOf('?');
                    if (query >= 0) target = target[..query];
                    target = target.TrimStart('/');

                    string? range = null;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0 && line[..colon].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                            range = line[(colon + 1)..].Trim();
                    }

                    Requests.Enqueue((target, range));

                    if (_failFirst.TryGetValue(target, out var remaining) && remaining > 0)
                    {
                        _failFirst[target] = remaining - 1;
                        await WriteResponseAsync(stream, 500, "Internal Server Error", Encoding.UTF8.GetBytes("retry"), range: null)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!_files.TryGetValue(target, out var data))
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", Array.Empty<byte>(), range: null).ConfigureAwait(false);
                        return;
                    }

                    _signatures.TryGetValue(target, out var signature);
                    _keyIds.TryGetValue(target, out var keyId);

                    byte[] body = data;
                    var status = 200;
                    var reason = "OK";
                    string? contentRange = null;
                    if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var spec = range[6..].Split('-');
                        if (spec.Length == 2 && long.TryParse(spec[0], out var start) && start >= 0 && start < data.Length)
                        {
                            var end = string.IsNullOrWhiteSpace(spec[1])
                                ? data.Length - 1
                                : Math.Min(data.Length - 1, long.Parse(spec[1]));
                            var length = (int)(end - start + 1);
                            body = new byte[length];
                            Array.Copy(data, start, body, 0, length);
                            status = 206;
                            reason = "Partial Content";
                            contentRange = $"bytes {start}-{end}/{data.Length}";
                        }
                    }

                    if (_slow.TryGetValue(target, out var slow))
                    {
                        await WriteSlowResponseAsync(stream, status, reason, body, contentRange,
                            signature, keyId, slow.Chunk, slow.DelayMs, _cts.Token).ConfigureAwait(false);
                        return;
                    }

                    if (_truncateAt.TryGetValue(target, out var truncate) && truncate < body.Length)
                    {
                        await WriteTruncatedResponseAsync(stream, status, reason, body, contentRange,
                            signature, keyId, truncate).ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(stream, status, reason, body, contentRange, signature, keyId)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Tests intentionally cancel connections; nothing to report.
                }
            }
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason,
            byte[] body, string? range, string? signature = null, string? keyId = null,
            bool headOnly = false)
        {
            var header = new StringBuilder();
            header.Append($"HTTP/1.1 {status} {reason}\r\n");
            header.Append("Connection: close\r\n");
            header.Append("Accept-Ranges: bytes\r\n");
            header.Append("Content-Type: application/octet-stream\r\n");
            header.Append($"Content-Length: {body.Length}\r\n");
            if (!string.IsNullOrWhiteSpace(range))
                header.Append($"Content-Range: {range}\r\n");
            if (!string.IsNullOrWhiteSpace(signature))
                header.Append($"X-Vellum-Signature: {signature}\r\n");
            if (!string.IsNullOrWhiteSpace(keyId))
                header.Append($"X-Vellum-Key-Id: {keyId}\r\n");
            header.Append("\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            await stream.WriteAsync(headerBytes).ConfigureAwait(false);
            if (!headOnly)
                await stream.WriteAsync(body).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteTruncatedResponseAsync(NetworkStream stream, int status, string reason,
            byte[] body, string? range, string? signature, string? keyId, int bytesToSend)
        {
            var header = new StringBuilder();
            header.Append($"HTTP/1.1 {status} {reason}\r\n");
            header.Append("Connection: close\r\n");
            header.Append("Accept-Ranges: bytes\r\n");
            header.Append("Content-Type: application/octet-stream\r\n");
            header.Append($"Content-Length: {body.Length}\r\n");
            if (!string.IsNullOrWhiteSpace(range))
                header.Append($"Content-Range: {range}\r\n");
            if (!string.IsNullOrWhiteSpace(signature))
                header.Append($"X-Vellum-Signature: {signature}\r\n");
            if (!string.IsNullOrWhiteSpace(keyId))
                header.Append($"X-Vellum-Key-Id: {keyId}\r\n");
            header.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString())).ConfigureAwait(false);
            await stream.WriteAsync(body.AsMemory(0, Math.Min(bytesToSend, body.Length))).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteSlowResponseAsync(NetworkStream stream, int status, string reason,
            byte[] body, string? range, string? signature, string? keyId, int chunk, int delayMs,
            CancellationToken ct)
        {
            var header = new StringBuilder();
            header.Append($"HTTP/1.1 {status} {reason}\r\n");
            header.Append("Connection: close\r\n");
            header.Append("Accept-Ranges: bytes\r\n");
            header.Append("Content-Type: application/octet-stream\r\n");
            header.Append($"Content-Length: {body.Length}\r\n");
            if (!string.IsNullOrWhiteSpace(range))
                header.Append($"Content-Range: {range}\r\n");
            if (!string.IsNullOrWhiteSpace(signature))
                header.Append($"X-Vellum-Signature: {signature}\r\n");
            if (!string.IsNullOrWhiteSpace(keyId))
                header.Append($"X-Vellum-Key-Id: {keyId}\r\n");
            header.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), ct).ConfigureAwait(false);

            for (var offset = 0; offset < body.Length; offset += chunk)
            {
                var length = Math.Min(chunk, body.Length - offset);
                await stream.WriteAsync(body.AsMemory(offset, length), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                if (offset + length < body.Length)
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await Task.CompletedTask;
        }
    }

    internal sealed class TestSigningKey : IDisposable
    {
        public TestSigningKey()
        {
            Rsa = RSA.Create(2048);
        }

        public RSA Rsa { get; }
        public string PublicKeyPem => Rsa.ExportSubjectPublicKeyInfoPem();

        public string Sign(byte[] payload)
            => Convert.ToBase64String(Rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        public void Dispose() => Rsa.Dispose();
    }

    internal sealed class StubDiskSpace : IDiskSpaceService
    {
        public long FreeBytes { get; set; } = long.MaxValue;

        public DiskSpaceRequirement CalculateRequirement(GameReleaseCandidate candidate, string targetInstallPath)
            => new() { AvailableFreeSpaceBytes = FreeBytes, RequiredFreeSpaceBytes = 0, TargetInstallPath = targetInstallPath };

        public DiskSpaceRequirement CalculateRequirement(GameDefinition definition, string targetInstallPath)
            => CalculateRequirement(new GameReleaseCandidate(), targetInstallPath);

        public long GetAvailableFreeSpace(string targetPath, out string driveRoot)
        {
            driveRoot = "TEST:";
            return FreeBytes;
        }
    }

    internal sealed class StubLauncher : IGameLauncher
    {
        public bool Running { get; set; }

        public Task<OperationResult> LaunchAsync(GameDefinition definition, GameInstallation installation)
            => Task.FromResult(OperationResult.Ok(installation.InstallPath));

        public GameRunStatus GetStatus(string gameId)
            => new() { GameId = gameId, State = Running ? GameRunState.Running : GameRunState.NotRunning };

        public bool IsRunning(string gameId) => Running;

#pragma warning disable CS0067
        public event EventHandler<GameRunStatus>? GameLaunched;
        public event EventHandler<GameRunStatus>? GameExited;
#pragma warning restore CS0067
    }
}
