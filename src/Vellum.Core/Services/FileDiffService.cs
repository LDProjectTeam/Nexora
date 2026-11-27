using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Computes the minimal set of files that differ between the installed tree and a
    /// remote manifest. Unchanged files are never queued for download.
    /// </summary>
    public class FileDiffService : IFileDiffService
    {
        private readonly ILogger<FileDiffService> _logger;

        public FileDiffService(ILogger<FileDiffService> logger)
        {
            _logger = logger;
        }

        public async Task<FileDiffResult> ComputeAsync(
            UpdateManifest manifest,
            string installRoot,
            IReadOnlyList<InstalledFileRecord>? previousManifest,
            bool verifyUnchangedFiles,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (!Directory.Exists(installRoot))
                throw new DirectoryNotFoundException($"The game folder does not exist: {installRoot}");

            var result = new FileDiffResult
            {
                GameId = manifest.GameId,
                Version = manifest.Version,
                BuildId = manifest.BuildId,
                Manifest = manifest,
                TotalManifestBytes = manifest.Files.Sum(f => f.Size)
            };

            var previousByPath = new Dictionary<string, InstalledFileRecord>(StringComparer.OrdinalIgnoreCase);
            if (previousManifest != null)
            {
                foreach (var file in previousManifest)
                {
                    if (string.IsNullOrWhiteSpace(file.Path))
                        continue;
                    var normalized = SafeRelativePath.NormalizeRelative(file.Path) ?? file.Path;
                    previousByPath[normalized] = file;
                }
            }

            var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filesToHash = new List<(ManifestFile File, FileChangeKind Kind, string FullPath)>();
            var completed = 0;
            var total = manifest.Files.Count;

            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                manifestPaths.Add(file.Path);

                if (!SafeRelativePath.TryResolve(installRoot, file.Path, out var fullPath))
                {
                    // The manifest is validated when it is fetched; this is defence in depth.
                    result.Changes.Add(new FileChange
                    {
                        Path = file.Path,
                        Kind = FileChangeKind.Corrupted,
                        Size = file.Size,
                        Sha256 = file.Sha256,
                        Url = file.Url
                    });
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(fullPath);
                    if (!info.Exists)
                    {
                        result.Changes.Add(Change(file, FileChangeKind.Added));
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Changes.Add(Change(file, FileChangeKind.Corrupted));
                    continue;
                }

                if (info.Length != file.Size)
                {
                    result.Changes.Add(Change(file, FileChangeKind.Modified));
                    continue;
                }

                var previousKnown = previousByPath.TryGetValue(file.Path, out var previous);
                var knownSameHash = previousKnown
                    && !string.IsNullOrWhiteSpace(previous!.Sha256)
                    && HashHelper.HashesMatch(previous.Sha256, file.Sha256);

                if (!verifyUnchangedFiles && knownSameHash)
                {
                    result.UnchangedFileCount++;
                    result.Changes.Add(Change(file, FileChangeKind.Unchanged));
                    completed++;
                    ReportProgress(progress, manifest.GameId, completed, total, file.Path);
                    continue;
                }

                filesToHash.Add((file, FileChangeKind.Unchanged, fullPath));
            }

            // Hashing the tree is I/O bound; a small degree of parallelism is enough.
            using var gate = new SemaphoreSlim(4, 4);
            var hashed = new List<(ManifestFile File, bool Match, string? Error)>();
            var hashTasks = new List<Task>();
            foreach (var entry in filesToHash)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                hashTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var actual = await HashHelper.ComputeSha256Async(entry.FullPath, ct).ConfigureAwait(false);
                        lock (hashed)
                            hashed.Add((entry.File, HashHelper.HashesMatch(actual, entry.File.Sha256), null));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lock (hashed)
                            hashed.Add((entry.File, false, ex.Message));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(hashTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            foreach (var entry in hashed)
            {
                completed++;
                if (entry.Match)
                {
                    result.UnchangedFileCount++;
                    result.Changes.Add(Change(entry.File, FileChangeKind.Unchanged));
                }
                else
                {
                    if (entry.Error != null)
                        _logger.LogWarning("Could not hash {File}: {Error}", entry.File.Path, entry.Error);
                    // A size-correct file with the wrong hash is corrupted when the
                    // previous manifest expected the new hash; otherwise it is a
                    // normal modified file.
                    var wasExpected = previousByPath.TryGetValue(entry.File.Path, out var previousRecord)
                        && !string.IsNullOrWhiteSpace(previousRecord.Sha256)
                        && HashHelper.HashesMatch(previousRecord.Sha256, entry.File.Sha256);
                    result.Changes.Add(Change(entry.File,
                        wasExpected ? FileChangeKind.Corrupted : FileChangeKind.Modified));
                }
                ReportProgress(progress, manifest.GameId, completed, total, entry.File.Path);
            }

            // Only remove files we know belonged to the previously installed manifest.
            // Arbitrary user files (saves, configs, mods) are never touched.
            var deletions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var deleted in manifest.DeletedFiles)
            {
                var normalized = SafeRelativePath.NormalizeRelative(deleted);
                if (normalized != null && !manifestPaths.Contains(normalized))
                    deletions.Add(normalized);
            }
            foreach (var previous in previousByPath.Keys)
            {
                if (!manifestPaths.Contains(previous))
                    deletions.Add(previous);
            }

            foreach (var deleted in deletions)
            {
                if (!SafeRelativePath.TryResolve(installRoot, deleted, out var fullPath))
                    continue;
                if (!File.Exists(fullPath))
                    continue;

                result.Changes.Add(new FileChange
                {
                    Path = deleted,
                    Kind = FileChangeKind.Deleted
                });
            }

            result.BytesToDownload = result.Downloads.Sum(c => c.Size);
            _logger.LogInformation(
                "Diff for {GameId} {Version}: {Added} added, {Modified} modified, {Corrupted} corrupted, {Deleted} deleted, {Unchanged} unchanged, {Bytes} bytes to download.",
                manifest.GameId, manifest.Version, result.AddedFileCount, result.ModifiedFileCount,
                result.CorruptedFileCount, result.DeletedFileCount, result.UnchangedFileCount,
                result.BytesToDownload);
            return result;
        }

        private static FileChange Change(ManifestFile file, FileChangeKind kind) => new()
        {
            Path = file.Path,
            Kind = kind,
            Size = file.Size,
            Sha256 = file.Sha256,
            Url = file.Url
        };

        private static void ReportProgress(IProgress<DownloadProgress>? progress, string gameId,
            int completed, int total, string currentFile)
        {
            progress?.Report(new DownloadProgress
            {
                GameId = gameId,
                Fraction = total > 0 ? (double)completed / total : 0,
                Phase = Localizer.T("UpdatePhaseChecking", "Checking files…"),
                PhaseKey = DownloadPhases.CheckingIntegrity,
                CurrentFile = currentFile
            });
        }
    }
}
