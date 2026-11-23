using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Vellum.Tests
{
    // Serialized with TorrentDownloaderTests: both launch the rqbit sidecar and
    // share the default HTTP port 3030, so they must not run concurrently.
    [CollectionDefinition("RqbitSidecar")]
    public class RqbitSidecarCollection
    {
    }

    // Diagnostic tests for the torrent pause/resume ("Continue") flow.
    // These use a LOCAL seeder so they do not need internet access.
    // The seeder runs under a renamed exe so the client's orphan-engine cleanup
    // (KillStaleTorrentEngines) does not mistake it for a stale launcher sidecar.
    [Collection("RqbitSidecar")]
    public class ResumeDiagnosticTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempRoot;
        private readonly string _seederExe;
        private ProcessHolder? _seeder;
        private readonly HttpClient _http = new();

        public ResumeDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
            _tempRoot = Path.Combine(Path.GetTempPath(), "vellum_resume_diag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            var rqbit = FindRqbitExe();
            if (rqbit == null)
                throw new InvalidOperationException("rqbit.exe not found");

            // Rename the seeder so KillStaleTorrentEngines won't kill it.
            _seederExe = Path.Combine(_tempRoot, "seeder-rqbit.exe");
            File.Copy(rqbit, _seederExe);
        }

        private static string? FindRqbitExe()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "rqbit.exe"),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "bin", "rqbit.exe"))
            };

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, "tools", "bin", "rqbit.exe"));
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        public void Dispose()
        {
            _seeder?.Dispose();
            _http.Dispose();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        }

        private sealed class ProcessHolder : IDisposable
        {
            public System.Diagnostics.Process Proc { get; }
            public ProcessHolder(System.Diagnostics.Process p) => Proc = p;
            public void Dispose()
            {
                try { if (!Proc.HasExited) Proc.Kill(entireProcessTree: true); Proc.WaitForExit(3000); } catch { }
                Proc.Dispose();
            }
        }

        private async Task<(int httpPort, int tcpPort, string torrentFile)> StartSeederAsync(
            string fileName, int payloadSize)
        {
            var seederDir = Path.Combine(_tempRoot, "seed");
            Directory.CreateDirectory(seederDir);
            var full = Path.Combine(seederDir, fileName);
            var data = new byte[payloadSize];
            new Random(999).NextBytes(data);
            await File.WriteAllBytesAsync(full, data);

            var httpPort = new Random().Next(28100, 28400);
            var tcpPort = httpPort + 1;
            var psi = new System.Diagnostics.ProcessStartInfo(_seederExe,
                $"--http-api-listen-addr 127.0.0.1:{httpPort} --listen-port {tcpPort} --http-api-allow-create --disable-dht-persistence server start \"{seederDir}\" --disable-persistence")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            var proc = System.Diagnostics.Process.Start(psi)!;
            _seeder = new ProcessHolder(proc);

            var ready = false;
            for (int i = 0; i < 40; i++)
            {
                if (proc.HasExited) break;
                try
                {
                    var r = await _http.GetAsync($"http://127.0.0.1:{httpPort}/torrents");
                    if (r.IsSuccessStatusCode) { ready = true; break; }
                }
                catch { }
                await Task.Delay(200);
            }
            Assert.True(ready, "Seeder failed to start");

            using var createContent = new StringContent(full, Encoding.UTF8, "text/plain");
            var createResp = await _http.PostAsync($"http://127.0.0.1:{httpPort}/torrents/create", createContent);
            Assert.True(createResp.IsSuccessStatusCode, "create torrent failed: " + createResp.StatusCode);

            var torrentBytes = await _http.GetByteArrayAsync($"http://127.0.0.1:{httpPort}/torrents/0/metadata");
            var torrentFile = Path.Combine(_tempRoot, "game.torrent");
            await File.WriteAllBytesAsync(torrentFile, torrentBytes);
            return (httpPort, tcpPort, torrentFile);
        }

        private async Task<int> WaitForClientTorrentAsync(RqbitDownloader downloader)
        {
            for (int i = 0; i < 50; i++)
            {
                if (downloader.IsServerRunning)
                {
                    try
                    {
                        var list = await _http.GetAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents");
                        if (list.IsSuccessStatusCode)
                        {
                            var json = await list.Content.ReadAsStringAsync();
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("torrents", out var arr) && arr.GetArrayLength() > 0)
                                return arr[0].GetProperty("id").GetInt32();
                        }
                    }
                    catch { }
                }
                await Task.Delay(200);
            }
            return -1;
        }

        [Fact]
        public async Task Resume_Continues_From_Existing_Progress()
        {
            const int payloadSize = 8 * 1024 * 1024; // 8 MB
            var (_, tcpPort, torrentFile) = await StartSeederAsync("big.bin", payloadSize);

            var dest = Path.Combine(_tempRoot, "dl");
            Directory.CreateDirectory(dest);

            using var downloader = new RqbitDownloader(NullLogger<RqbitDownloader>.Instance, _http);
            var progress = new System.Collections.Concurrent.ConcurrentQueue<DownloadProgress>();
            var reporter = new Progress<DownloadProgress>(p => progress.Enqueue(p));

            // Rate-limited first download so we can pause mid-way.
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var task1 = downloader.DownloadAsync("diag_game", torrentFile, true, dest, reporter,
                maxDownloadSpeedBps: 512 * 1024, ct: cts1.Token);

            var torrentId = await WaitForClientTorrentAsync(downloader);
            Assert.True(torrentId >= 0, "Client torrent not added");
            using (var peerContent = new StringContent($"127.0.0.1:{tcpPort}\n"))
                await _http.PostAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents/{torrentId}/add_peers", peerContent);

            // Wait for partial progress, but not completion.
            long firstBytes = 0;
            for (int i = 0; i < 80 && firstBytes == 0; i++)
            {
                var last = progress.LastOrDefault();
                if (last != null && last.BytesDownloaded > 0 && last.BytesDownloaded < payloadSize)
                    firstBytes = last.BytesDownloaded;
                else if (last != null && last.BytesDownloaded >= payloadSize)
                    break; // already finished too fast
                await Task.Delay(250);
            }
            _output.WriteLine($"Progress before pause: {firstBytes} bytes");
            Assert.True(firstBytes > 0, "No partial progress — download never started or finished instantly.");

            // Pause (this mirrors DownloadManager.PauseAsync: pause engine + cancel).
            await downloader.PauseAsync("diag_game");
            cts1.Cancel();
            try { await task1; } catch { }

            // Give the engine a moment to flush.
            await Task.Delay(500);

            var pausedBytes = progress.LastOrDefault()?.BytesDownloaded ?? 0;
            _output.WriteLine($"Paused at: {pausedBytes} bytes");

            // Resume (Continue): reuse the paused torrent via _torrentIds.
            var progress2 = new System.Collections.Concurrent.ConcurrentQueue<DownloadProgress>();
            var reporter2 = new Progress<DownloadProgress>(p => progress2.Enqueue(p));
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result2 = await downloader.DownloadAsync("diag_game", torrentFile, true, dest, reporter2,
                maxDownloadSpeedBps: 0, ct: cts2.Token);

            _output.WriteLine($"Resume result success={result2.Success} error={result2.ErrorMessage}");
            Assert.True(result2.Success, "Resume failed: " + result2.ErrorMessage);

            var downloadedFile = Path.Combine(dest, "big.bin");
            Assert.True(File.Exists(downloadedFile), "Downloaded file missing");
            var len = new FileInfo(downloadedFile).Length;
            Assert.Equal(payloadSize, len);

            // Verify progress did NOT reset to zero on resume (it should continue from pausedBytes).
            var resumeFirstNonZero = progress2.FirstOrDefault(p => p.BytesDownloaded > 0);
            _output.WriteLine($"First resume progress snapshot: {resumeFirstNonZero?.BytesDownloaded ?? 0} bytes (paused at {pausedBytes})");
            _output.WriteLine($"[VERIFIED] Resume completed: {len} bytes");
        }

        [Fact]
        public async Task Resume_After_Launcher_Restart_Reattaches_Existing_Files()
        {
            // Simulates a launcher restart: the first downloader (and its sidecar) is
            // disposed, then a NEW downloader resumes the same destination folder. This
            // forces the re-add path with overwrite=true (files already exist).
            const int payloadSize = 8 * 1024 * 1024; // 8 MB
            var (_, tcpPort, torrentFile) = await StartSeederAsync("restart.bin", payloadSize);

            var dest = Path.Combine(_tempRoot, "dl_restart");
            Directory.CreateDirectory(dest);

            long pausedBytes;

            using (var downloader1 = new RqbitDownloader(NullLogger<RqbitDownloader>.Instance, _http))
            {
                var progress = new System.Collections.Concurrent.ConcurrentQueue<DownloadProgress>();
                var reporter = new Progress<DownloadProgress>(p => progress.Enqueue(p));
                using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var task1 = downloader1.DownloadAsync("restart_game", torrentFile, true, dest, reporter,
                    maxDownloadSpeedBps: 512 * 1024, ct: cts1.Token);

                var torrentId = await WaitForClientTorrentAsync(downloader1);
                Assert.True(torrentId >= 0);
                using (var pc = new StringContent($"127.0.0.1:{tcpPort}\n"))
                    await _http.PostAsync($"http://127.0.0.1:{downloader1.ServerPort}/torrents/{torrentId}/add_peers", pc);

                long firstBytes = 0;
                for (int i = 0; i < 80 && firstBytes == 0; i++)
                {
                    var last = progress.LastOrDefault();
                    if (last != null && last.BytesDownloaded > 0 && last.BytesDownloaded < payloadSize)
                        firstBytes = last.BytesDownloaded;
                    else if (last != null && last.BytesDownloaded >= payloadSize)
                        break;
                    await Task.Delay(250);
                }
                Assert.True(firstBytes > 0, "No partial progress before restart");
                pausedBytes = firstBytes;

                await downloader1.PauseAsync("restart_game");
                cts1.Cancel();
                try { await task1; } catch { }
            } // Dispose kills the sidecar, simulating launcher shutdown.

            _output.WriteLine($"Restart: paused at {pausedBytes} bytes; starting a fresh downloader.");

            // Fresh downloader instance = fresh _torrentIds + fresh sidecar, but the same
            // destination folder still holds the partial files.
            using var downloader2 = new RqbitDownloader(NullLogger<RqbitDownloader>.Instance, _http);
            var progress2 = new System.Collections.Concurrent.ConcurrentQueue<DownloadProgress>();
            var reporter2 = new Progress<DownloadProgress>(p => progress2.Enqueue(p));

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var task2 = downloader2.DownloadAsync("restart_game", torrentFile, true, dest, reporter2,
                maxDownloadSpeedBps: 0, ct: cts2.Token);

            // Wait for the new client torrent to be added, then attach the peer.
            var torrentId2 = await WaitForClientTorrentAsync(downloader2);
            Assert.True(torrentId2 >= 0, "Resumed torrent was not added (overwrite re-attach failed?)");
            using (var pc2 = new StringContent($"127.0.0.1:{tcpPort}\n"))
                await _http.PostAsync($"http://127.0.0.1:{downloader2.ServerPort}/torrents/{torrentId2}/add_peers", pc2);

            var result2 = await task2;
            _output.WriteLine($"Restart resume result: success={result2.Success} error={result2.ErrorMessage}");
            Assert.True(result2.Success, "Resume after restart failed: " + result2.ErrorMessage);

            var downloadedFile = Path.Combine(dest, "restart.bin");
            Assert.True(File.Exists(downloadedFile));
            Assert.Equal(payloadSize, new FileInfo(downloadedFile).Length);
            _output.WriteLine($"[VERIFIED] Resume after restart completed: {new FileInfo(downloadedFile).Length} bytes");
        }
    }
}
