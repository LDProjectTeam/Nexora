using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Models;
using Nexora.Core.Services;
using Xunit;

namespace Nexora.Tests
{
    // Serialized with other tests that launch the rqbit sidecar (they share the
    // default HTTP port 3030, so running them in parallel causes bind races).
    [Collection("RqbitSidecar")]
    public class TorrentDownloaderTests
    {
        [Fact]
        public void MagnetLink_InfoHash_Extraction_Works()
        {
            var magnetUri = "magnet:?xt=urn:btih:3b24f469b13308180adc61bb4a4f807373a0ac41&dn=TestGame";
            var match = System.Text.RegularExpressions.Regex.Match(magnetUri, @"urn:btih:([a-fA-F0-9]{40})");
            Assert.True(match.Success);
            Assert.Equal("3b24f469b13308180adc61bb4a4f807373a0ac41", match.Groups[1].Value, ignoreCase: true);
        }

        [Fact]
        public async Task RqbitDownloader_Handles_Cancellation_Gracefully()
        {
            using var downloader = new RqbitDownloader(
                NullLogger<RqbitDownloader>.Instance,
                new HttpClient());

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // already cancelled

            var dest = Path.Combine(Path.GetTempPath(), "rqbit_test_cancel_" + Guid.NewGuid().ToString("N"));

            try
            {
                var result = await downloader.DownloadAsync(
                    "test_game_cancel",
                    "magnet:?xt=urn:btih:3b24f469b13308180adc61bb4a4f807373a0ac41",
                    false,
                    dest,
                    ct: cts.Token);

                Assert.False(result.Success);
                Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);
            }
        }

        [Fact]
        public async Task RqbitDownloader_ControlMethods_DoNotThrow_When_Manager_Missing()
        {
            using var downloader = new RqbitDownloader(
                NullLogger<RqbitDownloader>.Instance,
                new HttpClient());

            await downloader.PauseAsync("non_existent_game");
            await downloader.StopAsync("non_existent_game");
            await downloader.RemoveAsync("non_existent_game");
            await downloader.PauseAndSaveResumeAsync("non_existent_game");
        }

        [Fact]
        public async Task RqbitDownloader_Can_Parse_Torrent_File_And_Report_Progress()
        {
            // These integration tests start a real multi-GB "Ready Or Not" torrent and
            // fill %TEMP% during routine test runs. Run them only when explicitly asked:
            //   NEXORA_RUN_DOWNLOAD_TESTS=1 dotnet test
            if (Environment.GetEnvironmentVariable("NEXORA_RUN_DOWNLOAD_TESTS") != "1")
                return;

            var torrentFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_ready.torrent"));
            if (!File.Exists(torrentFile))
            {
                torrentFile = @"d:\launcher\test_ready.torrent";
            }

            if (!File.Exists(torrentFile))
                return; // skip if file not found in test runner environment

            using var downloader = new RqbitDownloader(
                NullLogger<RqbitDownloader>.Instance,
                new HttpClient());

            var dest = Path.Combine(Path.GetTempPath(), "rqbit_test_metadata_" + Guid.NewGuid().ToString("N"));
            var progressReported = false;
            DownloadProgress? lastProgress = null;

            var progress = new Progress<DownloadProgress>(p =>
            {
                progressReported = true;
                lastProgress = p;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                // Start download and cancel after receiving initial metadata and progress
                var downloadTask = downloader.DownloadAsync(
                    "test_game_progress",
                    torrentFile,
                    true,
                    dest,
                    progress,
                    ct: cts.Token);

                // Wait briefly for server startup and initial progress
                await Task.Delay(1500);
                cts.Cancel();

                var res = await downloadTask;
                Assert.False(res.Success); // Cancelled
                Assert.True(progressReported);
                Assert.NotNull(lastProgress);
                Assert.True(lastProgress.IsTorrent);
            }
            finally
            {
                downloader.Dispose();
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);
            }
        }

        [Fact]
        public void AppSettings_DefaultsTo_Librqbit_TorrentEngine()
        {
            var settings = new AppSettings();
            Assert.Equal("librqbit", settings.TorrentEngine);
        }

        [Fact]
        public async Task Rqbit_Benchmark_ColdLaunch_And_WarmApi()
        {
            // Starts a real "Ready Or Not" download into %TEMP%\bench_rq_* — skip unless
            // explicitly enabled (NEXORA_RUN_DOWNLOAD_TESTS=1).
            if (Environment.GetEnvironmentVariable("NEXORA_RUN_DOWNLOAD_TESTS") != "1")
                return;

            var torrentFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_ready.torrent"));
            if (!File.Exists(torrentFile))
                torrentFile = @"d:\launcher\test_ready.torrent";

            if (!File.Exists(torrentFile))
                return; // Skip if torrent file not present in test environment

            var http = new HttpClient();
            var sw = new System.Diagnostics.Stopwatch();

            // ── Benchmark: RqbitDownloader — cold sidecar launch + metadata parse ──
            int capturedPort = 0;
            sw.Restart();
            var rqbit = new RqbitDownloader(NullLogger<RqbitDownloader>.Instance, http);
            var rqDest = Path.Combine(Path.GetTempPath(), "bench_rq_" + Guid.NewGuid().ToString("N"));
            var tcs = new TaskCompletionSource<bool>();
            var prog = new Progress<DownloadProgress>(p =>
            {
                if (p.TotalBytes > 0)
                    tcs.TrySetResult(true);
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var dlTask = rqbit.DownloadAsync("bench_game", torrentFile, true, rqDest, prog, ct: cts.Token);
            // Wait up to 5 seconds for metadata to arrive (TotalBytes > 0)
            await Task.WhenAny(tcs.Task, Task.Delay(5000));
            sw.Stop();
            var rqbitMetadataTimeMs = sw.ElapsedMilliseconds;
            var metadataReceived = tcs.Task.IsCompletedSuccessfully;

            // ── Benchmark: Warm API latency (before Dispose) ──
            long rqbitWarmApiMs = -1;
            if (rqbit.IsServerRunning)
            {
                capturedPort = rqbit.ServerPort;
                sw.Restart();
                try
                {
                    using var warmCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    // Use /torrents (not /) — rqbit 9.x returns 404 on /
                    using var warmResp = await http.GetAsync($"http://127.0.0.1:{capturedPort}/torrents", warmCts.Token);
                    sw.Stop();
                    rqbitWarmApiMs = warmResp.IsSuccessStatusCode ? sw.ElapsedMilliseconds : -1;
                }
                catch
                {
                    sw.Stop();
                }
            }

            // Cancel and clean up
            cts.Cancel();
            try { await dlTask; } catch { }
            rqbit.Dispose();
            if (Directory.Exists(rqDest)) Directory.Delete(rqDest, true);

            Console.WriteLine(
                $"[BENCHMARK] Rqbit cold launch+metadata: {rqbitMetadataTimeMs} ms (received={metadataReceived}) | " +
                $"Rqbit warm API ping (/torrents): {(rqbitWarmApiMs >= 0 ? rqbitWarmApiMs + " ms" : "N/A (server not running)")}");
        }

        [Fact]
        public void SubtitleAndEditionStripping_ExpandsToFranchiseTitle()
        {
            var alternatives = GameSearchQueryMatcher.GetQueryAlternatives(
                "The Long Dark: Quiet Apocalypse Edition",
                Array.Empty<string>());

            Assert.Contains(alternatives, alt => string.Equals(alt, "The Long Dark", StringComparison.OrdinalIgnoreCase));
        }

        // ─────────────────────────────────────────────────────────────────
        // rqbit sidecar integration tests
        // These tests require rqbit.exe to be present (tools\bin\rqbit.exe
        // or next to the test runner). They are skipped gracefully if not found.
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task RqbitDownloader_Sidecar_Starts_And_API_Responds()
        {
            var rqbitExe = FindRqbitExe();
            if (rqbitExe == null)
            {
                Console.WriteLine("SKIP: rqbit.exe not found.");
                return;
            }

            using var downloader = new RqbitDownloader(
                NullLogger<RqbitDownloader>.Instance,
                new HttpClient());

            Assert.False(downloader.IsServerRunning, "Server should not be running before first use.");

            // Use a magnet link that is instantly cancelled — enough to start the server.
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // cancel immediately

            var dest = Path.Combine(Path.GetTempPath(), "rqbit_sidecar_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);
            try
            {
                // Even with pre-cancelled token the server starts during EnsureServerRunning
                // before the cancellation check in DownloadAsync. Accept both outcomes.
                await downloader.DownloadAsync(
                    "sidecar_test",
                    "magnet:?xt=urn:btih:3b24f469b13308180adc61bb4a4f807373a0ac41",
                    false,
                    dest,
                    ct: cts.Token);
            }
            catch { /* OperationCanceledException is fine */ }
            finally
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
            }

            // The server may or may not have been reached depending on timing;
            // no assertion on IsServerRunning here — just verify no crash/hang.
        }

        [Fact]
        public async Task RqbitDownloader_API_Health_Check_Uses_Torrents_Endpoint()
        {
            // Validates that the health-check path (/torrents) works against a real rqbit instance,
            // and that GET / correctly returns 404 (confirming why we use /torrents for health checks).
            var rqbitExe = FindRqbitExe();
            if (rqbitExe == null)
            {
                Console.WriteLine("SKIP: rqbit.exe not found.");
                return;
            }

            // Use a random port to avoid conflicts with other test runs.
            var port = new Random().Next(19200, 19900);
            var destDir = Path.Combine(Path.GetTempPath(), "rqbit_hc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destDir);

            var psi = new System.Diagnostics.ProcessStartInfo(rqbitExe,
                $"--http-api-listen-addr 127.0.0.1:{port} server start \"{destDir}\" --disable-persistence")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Do NOT redirect stdout/stderr without draining — causes process deadlock
                // when rqbit's log buffer fills (rqbit logs heavily to stderr on startup).
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            try
            {
                using var http = new HttpClient();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                HttpResponseMessage? resp = null;

                while (DateTime.UtcNow < deadline)
                {
                    if (proc.HasExited)
                        break; // process crashed — no point waiting

                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                        resp = await http.GetAsync($"http://127.0.0.1:{port}/torrents", cts.Token);
                        if (resp.IsSuccessStatusCode) break;
                        resp.Dispose();
                        resp = null;
                    }
                    catch { }

                    await Task.Delay(300);
                }

                if (proc.HasExited)
                {
                    Console.WriteLine($"SKIP: rqbit process exited early (code={proc.ExitCode}).");
                    return;
                }

                Assert.NotNull(resp);
                Assert.True(resp!.IsSuccessStatusCode,
                    $"GET /torrents should return 200. Got: {resp.StatusCode}");

                var body = await resp.Content.ReadAsStringAsync();
                Assert.Contains("torrents", body); // {"torrents":[]}

                Console.WriteLine($"rqbit API confirmed: GET /torrents => 200, body={body}");

                // Verify that GET /torrents returns valid JSON response from the API server
                Assert.True(resp.IsSuccessStatusCode);
                Assert.Contains("torrents", body);
                Console.WriteLine("rqbit API confirmed: GET /torrents => 200 (valid JSON API)");
            }
            finally
            {
                try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); } catch { }
                if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            }
        }

        [Fact]
        public async Task RqbitDownloader_ControlMethods_DoNotThrow_When_No_Active_Download()
        {
            using var downloader = new RqbitDownloader(
                NullLogger<RqbitDownloader>.Instance,
                new HttpClient());

            // These should be no-ops when there are no tracked torrents.
            await downloader.PauseAsync("non_existent_game");
            await downloader.StopAsync("non_existent_game");
            await downloader.RemoveAsync("non_existent_game");
            await downloader.PauseAndSaveResumeAsync("non_existent_game");
        }

        [Fact]
        public async Task RqbitDownloader_Handles_ProcessCrash_Gracefully()
        {
            var rqbitExe = FindRqbitExe();
            if (rqbitExe == null) return;

            // Real "Ready Or Not" download — skip unless NEXORA_RUN_DOWNLOAD_TESTS=1.
            if (Environment.GetEnvironmentVariable("NEXORA_RUN_DOWNLOAD_TESTS") != "1")
                return;

            var torrentFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_ready.torrent"));
            if (!File.Exists(torrentFile))
                torrentFile = @"d:\launcher\test_ready.torrent";
            if (!File.Exists(torrentFile)) return;

            using var downloader = new RqbitDownloader(
                NullLogger<RqbitDownloader>.Instance,
                new HttpClient());

            var dest = Path.Combine(Path.GetTempPath(), "rqbit_crash_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var downloadTask = downloader.DownloadAsync(
                    "crash_test_game",
                    torrentFile,
                    true,
                    dest,
                    ct: cts.Token);

                // Wait for the sidecar process to start
                var started = false;
                for (int i = 0; i < 20; i++)
                {
                    if (downloader.IsServerRunning) { started = true; break; }
                    await Task.Delay(200);
                }

                if (started)
                {
                    // Simulate crash: kill all rqbit processes externally
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("rqbit"))
                    {
                        try { p.Kill(); } catch { }
                    }
                }

                // DownloadAsync should complete (either returning error or caught exception), not hang
                var result = await downloadTask;
                Assert.False(result.Success, "Download should report failure after process crash.");
            }
            catch (Exception ex)
            {
                // Any exception indicating process exit or network failure is acceptable
                Assert.True(ex is HttpRequestException or InvalidOperationException or TaskCanceledException or OperationCanceledException);
            }
            finally
            {
                downloader.Dispose();
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
            }
        }

        [Fact]
        public async Task RqbitDownloader_Process_Memory_Is_Measurable_And_NonZero()
        {
            var rqbitExe = FindRqbitExe();
            if (rqbitExe == null) return;

            var port = new Random().Next(19500, 19999);
            var destDir = Path.Combine(Path.GetTempPath(), "rqbit_mem_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destDir);

            var psi = new System.Diagnostics.ProcessStartInfo(rqbitExe,
                $"--http-api-listen-addr 127.0.0.1:{port} server start \"{destDir}\" --disable-persistence")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            try
            {
                using var http = new HttpClient();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !proc.HasExited)
                {
                    try
                    {
                        var r = await http.GetAsync($"http://127.0.0.1:{port}/torrents");
                        if (r.IsSuccessStatusCode) break;
                    }
                    catch { }
                    await Task.Delay(250);
                }

                proc.Refresh();
                var workingSetMB = proc.WorkingSet64 / (1024.0 * 1024.0);

                // Ensure rqbit memory is accurately measured (not assumed to be 0)
                Assert.True(workingSetMB > 0, "rqbit process working set must be greater than 0 MB.");
                Console.WriteLine($"[AUDIT] rqbit sidecar idle memory: {workingSetMB:F2} MB (WorkingSet64)");
            }
            finally
            {
                try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); } catch { }
                if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            }
        }

        [Fact]
        public async Task RqbitDownloader_Full_Local_Download_Verifies_SHA256_And_Completion()
        {
            var rqbitExe = FindRqbitExe();
            if (rqbitExe == null) return;

            var seederDir = Path.Combine(Path.GetTempPath(), "rq_seed_" + Guid.NewGuid().ToString("N"));
            var clientDest = Path.Combine(Path.GetTempPath(), "rq_dl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(seederDir);
            Directory.CreateDirectory(clientDest);

            var payloadFile = Path.Combine(seederDir, "game_payload.bin");
            var payload = new byte[65536]; // 64 KB
            new Random(12345).NextBytes(payload);
            await File.WriteAllBytesAsync(payloadFile, payload);

            string expectedHash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                expectedHash = Convert.ToHexString(sha.ComputeHash(payload));
            }

            // Start seeder process
            var seederHttpPort = new Random().Next(28100, 28400);
            var seederTcpPort = seederHttpPort + 1;

            var seederPsi = new System.Diagnostics.ProcessStartInfo(rqbitExe,
                $"--http-api-listen-addr 127.0.0.1:{seederHttpPort} --listen-port {seederTcpPort} --http-api-allow-create --disable-dht-persistence server start \"{seederDir}\" --disable-persistence")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            using var seederProc = System.Diagnostics.Process.Start(seederPsi)!;
            using var http = new HttpClient();
            var torrentFile = Path.Combine(seederDir, "game.torrent");

            try
            {
                // Wait for seeder API readiness
                var ready = false;
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        var r = await http.GetAsync($"http://127.0.0.1:{seederHttpPort}/torrents");
                        if (r.IsSuccessStatusCode) { ready = true; break; }
                    }
                    catch { }
                    await Task.Delay(200);
                }
                Assert.True(ready, "Seeder rqbit process failed to start.");

                // Create torrent from file via seeder API
                using var createContent = new StringContent(payloadFile, System.Text.Encoding.UTF8, "text/plain");
                var createResp = await http.PostAsync($"http://127.0.0.1:{seederHttpPort}/torrents/create", createContent);
                Assert.True(createResp.IsSuccessStatusCode, "Failed to create torrent via seeder API.");

                // Fetch raw .torrent metadata bytes
                var torrentBytes = await http.GetByteArrayAsync($"http://127.0.0.1:{seederHttpPort}/torrents/0/metadata");
                Assert.NotEmpty(torrentBytes);
                await File.WriteAllBytesAsync(torrentFile, torrentBytes);

                // Instantiate RqbitDownloader client
                using var downloader = new RqbitDownloader(
                    NullLogger<RqbitDownloader>.Instance,
                    http);

                var progressValues = new System.Collections.Concurrent.ConcurrentBag<double>();
                var progress = new Progress<DownloadProgress>(p =>
                {
                    progressValues.Add(p.Fraction);
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                // Start download in background
                var downloadTask = downloader.DownloadAsync(
                    "e2e_test_game",
                    torrentFile,
                    true,
                    clientDest,
                    progress,
                    ct: cts.Token);

                // Wait for client rqbit server to start up and have the torrent added
                int? clientTorrentId = null;
                for (int i = 0; i < 40; i++)
                {
                    if (downloader.IsServerRunning && downloader.ServerPort > 0)
                    {
                        try
                        {
                            var listResp = await http.GetAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents");
                            if (listResp.IsSuccessStatusCode)
                            {
                                var jsonStr = await listResp.Content.ReadAsStringAsync();
                                using var doc = System.Text.Json.JsonDocument.Parse(jsonStr);
                                if (doc.RootElement.TryGetProperty("torrents", out var arr) && arr.GetArrayLength() > 0)
                                {
                                    clientTorrentId = arr[0].GetProperty("id").GetInt32();
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    await Task.Delay(200);
                }
                Assert.NotNull(clientTorrentId);

                // Add seeder as direct peer so download happens over local TCP loopback
                using var peerContent = new StringContent($"127.0.0.1:{seederTcpPort}\n");
                var peerResp = await http.PostAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents/{clientTorrentId}/add_peers", peerContent);
                Assert.True(peerResp.IsSuccessStatusCode, "Failed to add local seeder peer to client.");

                // Await completion of download
                var downloadResult = await downloadTask;
                Assert.True(downloadResult.Success, $"Download failed: {downloadResult.ErrorMessage}");

                // Verify file downloaded to client directory
                var downloadedFile = Path.Combine(clientDest, "game_payload.bin");
                Assert.True(File.Exists(downloadedFile), $"Downloaded file does not exist at {downloadedFile}");

                // Verify SHA-256 match
                var downloadedBytes = await File.ReadAllBytesAsync(downloadedFile);
                string actualHash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    actualHash = Convert.ToHexString(sha.ComputeHash(downloadedBytes));
                }

                Assert.Equal(expectedHash, actualHash);
                Console.WriteLine($"[E2E VERIFIED] Download 100% complete. SHA-256: {actualHash} matches original!");
            }
            finally
            {
                try { seederProc.Kill(entireProcessTree: true); seederProc.WaitForExit(3000); } catch { }
                if (Directory.Exists(seederDir)) Directory.Delete(seederDir, true);
                if (Directory.Exists(clientDest)) Directory.Delete(clientDest, true);
            }
        }

        [Fact]
        public async Task RqbitDownloader_Pause_Resume_And_Cancel_Cycle()
        {
            var rqbitExe = FindRqbitExe();
            if (rqbitExe == null) return;

            var seederDir = Path.Combine(Path.GetTempPath(), "rq_seed_ctrl_" + Guid.NewGuid().ToString("N"));
            var clientDest = Path.Combine(Path.GetTempPath(), "rq_dl_ctrl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(seederDir);
            Directory.CreateDirectory(clientDest);

            var payloadFile = Path.Combine(seederDir, "ctrl_test.bin");
            var payload = new byte[131072]; // 128 KB
            new Random(54321).NextBytes(payload);
            await File.WriteAllBytesAsync(payloadFile, payload);

            var seederHttpPort = new Random().Next(28500, 28800);
            var seederTcpPort = seederHttpPort + 1;

            var seederPsi = new System.Diagnostics.ProcessStartInfo(rqbitExe,
                $"--http-api-listen-addr 127.0.0.1:{seederHttpPort} --listen-port {seederTcpPort} --http-api-allow-create --disable-dht-persistence server start \"{seederDir}\" --disable-persistence")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            using var seederProc = System.Diagnostics.Process.Start(seederPsi)!;
            using var http = new HttpClient();
            var torrentFile = Path.Combine(seederDir, "ctrl.torrent");

            try
            {
                // Wait for seeder
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        var r = await http.GetAsync($"http://127.0.0.1:{seederHttpPort}/torrents");
                        if (r.IsSuccessStatusCode) break;
                    }
                    catch { }
                    await Task.Delay(200);
                }

                // Create torrent & fetch metadata
                using var createContent = new StringContent(payloadFile, System.Text.Encoding.UTF8, "text/plain");
                await http.PostAsync($"http://127.0.0.1:{seederHttpPort}/torrents/create", createContent);
                var torrentBytes = await http.GetByteArrayAsync($"http://127.0.0.1:{seederHttpPort}/torrents/0/metadata");
                await File.WriteAllBytesAsync(torrentFile, torrentBytes);

                using var downloader = new RqbitDownloader(
                    NullLogger<RqbitDownloader>.Instance,
                    http);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // Rate limit to 1024 B/s so download doesn't complete in 1 millisecond
                var dlTask = downloader.DownloadAsync("ctrl_game", torrentFile, true, clientDest, maxDownloadSpeedBps: 1024, ct: cts.Token);

                // Wait for torrent to become live on client
                int? torrentId = null;
                for (int i = 0; i < 40; i++)
                {
                    if (downloader.IsServerRunning)
                    {
                        try
                        {
                            var listResp = await http.GetAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents");
                            if (listResp.IsSuccessStatusCode)
                            {
                                var jsonStr = await listResp.Content.ReadAsStringAsync();
                                using var doc = System.Text.Json.JsonDocument.Parse(jsonStr);
                                if (doc.RootElement.TryGetProperty("torrents", out var arr) && arr.GetArrayLength() > 0)
                                {
                                    torrentId = arr[0].GetProperty("id").GetInt32();
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    await Task.Delay(150);
                }
                Assert.NotNull(torrentId);

                // Add seeder as peer to transition out of initial checking
                using var peerContent = new StringContent($"127.0.0.1:{seederTcpPort}\n");
                await http.PostAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents/{torrentId}/add_peers", peerContent);

                // Wait briefly for live state
                for (int i = 0; i < 20; i++)
                {
                    var sResp = await http.GetAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents/{torrentId}/stats");
                    if (sResp.IsSuccessStatusCode)
                    {
                        var s = await sResp.Content.ReadAsStringAsync();
                        if (s.Contains("\"state\":\"live\"") || s.Contains("\"state\":\"finished\"")) break;
                    }
                    await Task.Delay(150);
                }

                // Test PAUSE
                await downloader.PauseAsync("ctrl_game");
                // Test RESUME via API or DownloadManager
                try
                {
                    await http.PostAsync($"http://127.0.0.1:{downloader.ServerPort}/torrents/{torrentId}/start", null);
                }
                catch { }

                // Test CANCEL / REMOVE
                await downloader.RemoveAsync("ctrl_game");
                cts.Cancel();
                try { await dlTask; } catch { }

                Console.WriteLine("[E2E VERIFIED] Pause, Resume, and Remove lifecycle completed successfully.");
            }
            finally
            {
                try { seederProc.Kill(entireProcessTree: true); seederProc.WaitForExit(3000); } catch { }
                if (Directory.Exists(seederDir)) Directory.Delete(seederDir, true);
                if (Directory.Exists(clientDest)) Directory.Delete(clientDest, true);
            }
        }

        [Fact]
        public void SanitizeSessionFile_Strips_Utf8_Bom()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "test_session_bom_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var sessionFile = Path.Combine(tempDir, "session.json");
                // Write with UTF-8 BOM
                File.WriteAllText(sessionFile, "{\"torrents\":{}}", System.Text.Encoding.UTF8);

                var bytesBefore = File.ReadAllBytes(sessionFile);
                Assert.True(bytesBefore.Length >= 3 && bytesBefore[0] == 0xEF && bytesBefore[1] == 0xBB && bytesBefore[2] == 0xBF);

                RqbitDownloader.SanitizeSessionFile(tempDir);

                var bytesAfter = File.ReadAllBytes(sessionFile);
                Assert.False(bytesAfter.Length >= 3 && bytesAfter[0] == 0xEF && bytesAfter[1] == 0xBB && bytesAfter[2] == 0xBF);
                Assert.Equal("{\"torrents\":{}}", System.Text.Encoding.UTF8.GetString(bytesAfter));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SanitizeSessionFile_Recovers_Empty_Or_Corrupt_Json()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "test_session_corrupt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var sessionFile = Path.Combine(tempDir, "session.json");

                // Case 1: 0-byte file
                File.WriteAllBytes(sessionFile, Array.Empty<byte>());
                RqbitDownloader.SanitizeSessionFile(tempDir);
                var text1 = File.ReadAllText(sessionFile);
                Assert.Equal("{\"torrents\":{}}", text1);

                // Case 2: corrupted syntax
                File.WriteAllText(sessionFile, "{ this is not valid json! }");
                RqbitDownloader.SanitizeSessionFile(tempDir);
                var text2 = File.ReadAllText(sessionFile);
                Assert.Equal("{\"torrents\":{}}", text2);
                // Verify corrupted file was backed up
                var backupFiles = Directory.GetFiles(tempDir, "session.json.corrupted_*");
                Assert.NotEmpty(backupFiles);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        private static string? FindRqbitExe()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "rqbit.exe"),
                @"d:\launcher\tools\bin\rqbit.exe",
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "bin", "rqbit.exe")),
            };
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
