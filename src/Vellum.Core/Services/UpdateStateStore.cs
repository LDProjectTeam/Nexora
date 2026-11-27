using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Persists the update queue, per-game versions, installed file manifests, error
    /// history and launcher update state. Writes are atomic (temp file + replace) so a
    /// crash can never leave a half-written state file.
    /// </summary>
    public class UpdateStateStore : IUpdateStateStore
    {
        private readonly ILogger<UpdateStateStore> _logger;
        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private string? _lastSavedJson;
        private bool _loaded;

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter() }
        };

        public UpdatePersistedState State { get; private set; } = new();

        public UpdateStateStore(ILogger<UpdateStateStore> logger)
        {
            _logger = logger;
        }

        public async Task LoadAsync(CancellationToken ct = default)
        {
            if (_loaded)
                return;

            PathHelper.EnsureDirectory(PathHelper.UpdatesDir);
            PathHelper.EnsureDirectory(PathHelper.UpdateManifestsDir);
            PathHelper.EnsureDirectory(PathHelper.UpdateJournalsDir);
            PathHelper.EnsureDirectory(PathHelper.UpdateRollbackDir);
            PathHelper.EnsureDirectory(PathHelper.UpdateLocksDir);
            PathHelper.EnsureDirectory(PathHelper.LauncherUpdateDir);

            var path = PathHelper.UpdateStateFile;
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    State = JsonSerializer.Deserialize<UpdatePersistedState>(json, _json) ?? new();
                    State.Games = new Dictionary<string, GameUpdateRecord>(
                        State.Games ?? new(), StringComparer.OrdinalIgnoreCase);
                    State.Queue ??= new List<GameUpdateQueueEntry>();
                    State.ErrorHistory ??= new List<UpdateErrorRecord>();
                    _lastSavedJson = json;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read {Path}; starting with empty update state.", path);
                    TryPreserveCorruptState(path);
                    State = new UpdatePersistedState();
                }
            }

            _loaded = true;
        }

        public async Task SaveAsync(CancellationToken ct = default)
        {
            PathHelper.EnsureDirectory(PathHelper.UpdatesDir);
            await _saveLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                TrimErrorHistory();
                var json = JsonSerializer.Serialize(State, _json);
                var path = PathHelper.UpdateStateFile;
                if (string.Equals(json, _lastSavedJson, StringComparison.Ordinal) && File.Exists(path))
                    return;

                var temp = path + ".tmp";
                await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        File.Copy(temp, path, overwrite: true);
                        TryDelete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }

                _lastSavedJson = json;
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public async Task SaveManifestAsync(string gameId, UpdateManifest manifest, CancellationToken ct = default)
        {
            var path = GetManifestPath(gameId);
            PathHelper.EnsureDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            var json = JsonSerializer.Serialize(manifest, _json);
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            if (File.Exists(path))
                File.Move(temp, path, overwrite: true);
            else
                File.Move(temp, path);
        }

        public async Task<UpdateManifest?> LoadManifestAsync(string gameId, CancellationToken ct = default)
        {
            var path = GetManifestPath(gameId);
            if (!File.Exists(path))
                return null;
            try
            {
                var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<UpdateManifest>(json, _json);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the persisted manifest for {GameId}; it will be fetched again.", gameId);
                return null;
            }
        }

        public Task DeleteManifestAsync(string gameId)
        {
            TryDelete(GetManifestPath(gameId));
            return Task.CompletedTask;
        }

        public Task<IDisposable?> TryAcquireGameLockAsync(string gameId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PathHelper.EnsureDirectory(PathHelper.UpdateLocksDir);
            var safe = PathHelper.SanitizeFileName(gameId);
            if (string.IsNullOrWhiteSpace(safe))
                return Task.FromResult<IDisposable?>(null);

            var path = Path.Combine(PathHelper.UpdateLocksDir, safe + ".lock");
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                return Task.FromResult<IDisposable?>(new FileLock(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Another process already updates {GameId}: {Message}", gameId, ex.Message);
                return Task.FromResult<IDisposable?>(null);
            }
        }

        public void RecordError(string gameId, string message)
        {
            State.ErrorHistory.Add(new UpdateErrorRecord
            {
                GameId = gameId,
                Message = message,
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }

        private void TrimErrorHistory()
        {
            if (State.ErrorHistory.Count <= 100)
                return;
            State.ErrorHistory.RemoveRange(0, State.ErrorHistory.Count - 100);
        }

        private static string GetManifestPath(string gameId)
        {
            var safe = PathHelper.SanitizeFileName(gameId);
            if (string.IsNullOrWhiteSpace(safe))
                throw new ArgumentException("A valid game id is required.", nameof(gameId));
            return Path.Combine(PathHelper.UpdateManifestsDir, safe + ".json");
        }

        private void TryPreserveCorruptState(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Copy(path, path + ".corrupt." + DateTime.UtcNow.Ticks, overwrite: true);
            }
            catch
            {
                // Best effort only.
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
                // Best effort only.
            }
        }

        private sealed class FileLock : IDisposable
        {
            private FileStream? _stream;

            public FileLock(FileStream stream)
            {
                _stream = stream;
            }

            public void Dispose()
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
