using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Microsoft.Extensions.Logging;

namespace Vellum.Core.Services
{
    public class GameSearchAggregator : IGameSearchAggregator
    {
        private readonly List<IGameSource> _sources = new();
        private readonly IVersionSelector _versionSelector;
        private readonly ILogger<GameSearchAggregator> _logger;
        private readonly ConcurrentDictionary<string, string> _knownGameTitles = new(StringComparer.Ordinal);

        private static readonly string LatestCacheFile =
            Path.Combine(PathHelper.AppDataDir, "cache", "latest-releases.json");
        private static readonly object LatestCacheLock = new();
        private static readonly JsonSerializerOptions LatestCacheJson = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public IReadOnlyList<IGameSource> Sources => _sources.AsReadOnly();

        public GameSearchAggregator(
            IEnumerable<IGameSource> sources,
            ITitleNormalizer normalizer,
            IVersionSelector versionSelector,
            ILogger<GameSearchAggregator> logger)
        {
            _sources.AddRange(sources);
            ArgumentNullException.ThrowIfNull(normalizer);
            _versionSelector = versionSelector;
            _logger = logger;
        }

        public void RegisterSource(IGameSource source)
        {
            if (!_sources.Any(s => s.SourceId == source.SourceId))
            {
                _sources.Add(source);
                _logger.LogInformation("Registered game source: {SourceId} ({Name})", source.SourceId, source.DisplayName);
            }
        }

        public async Task<IReadOnlyList<GameReleaseCandidate>> SearchAllSourcesAsync(string query, CancellationToken ct = default)
        {
            var (results, _) = await SearchAllSourcesWithErrorsAsync(query, ct);
            return results;
        }

        private async Task<(IReadOnlyList<GameReleaseCandidate> Results, List<string> Errors)> SearchAllSourcesWithErrorsAsync(
            string query,
            CancellationToken ct = default)
        {
            var primary = await SearchSourcesOnceAsync(query, ct);
            RememberGameTitles(primary.Results);

            var alternatives = GameSearchQueryMatcher.GetQueryAlternatives(
                query, _knownGameTitles.Values, allowFuzzyCorrection: primary.Results.Count == 0);
            if (alternatives.Count == 0)
                return primary;

            var limitedAlternatives = alternatives.Take(3).ToList();
            var alternateSearches = await Task.WhenAll(limitedAlternatives.Select(alternative =>
                SearchSourcesOnceAsync(alternative, ct)));
            var allResults = primary.Results.ToList();
            var allErrors = primary.Errors.ToList();
            foreach (var alternate in alternateSearches)
            {
                allResults.AddRange(alternate.Results);
                allErrors.AddRange(alternate.Errors);
                RememberGameTitles(alternate.Results);
            }

            var uniqueResults = allResults
                .GroupBy(candidate => !string.IsNullOrWhiteSpace(candidate.DetailsUrl)
                    ? $"{candidate.SourceId}|{candidate.DetailsUrl}"
                    : $"{candidate.SourceId}|{GetTitleKey(candidate.CleanTitle)}|{candidate.VersionType}|{candidate.Version}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(c => c.Seeders > 0)
                    .ThenByDescending(c => c.Seeders)
                    .First())
                .ToList();
            return (uniqueResults, allErrors.Distinct(StringComparer.Ordinal).ToList());
        }

        private async Task<(IReadOnlyList<GameReleaseCandidate> Results, List<string> Errors)> SearchSourcesOnceAsync(
            string query,
            CancellationToken ct)
        {
            var allResults = new ConcurrentBag<GameReleaseCandidate>();
            var errors = new ConcurrentBag<string>();
            var activeSources = _sources.Where(s => s.IsEnabled).ToList();

            if (activeSources.Count == 0)
            {
                _logger.LogWarning("No game sources enabled for search.");
                errors.Add("Нет активных источников для поиска.");
                return (Array.Empty<GameReleaseCandidate>(), errors.ToList());
            }

            var tasks = activeSources.Select(async source =>
            {
                try
                {
                    using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sourceTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                    var results = await source.SearchAsync(query, sourceTimeout.Token);
                    foreach (var item in results)
                    {
                        allResults.Add(item);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Source {SourceId} search failed for query '{Query}'", source.SourceId, query);
                    errors.Add($"Источник '{source.DisplayName}' недоступен: {ex.Message}");
                }
            });

            await Task.WhenAll(tasks);
            ct.ThrowIfCancellationRequested();
            return (allResults.ToList(), errors.ToList());
        }

        private void RememberGameTitles(IEnumerable<GameReleaseCandidate> candidates)
        {
            foreach (var candidate in candidates)
            {
                RememberGameTitle(candidate.CleanTitle);
                RememberGameTitle(candidate.AltTitle);
            }
        }

        private void RememberGameTitle(string title)
        {
            var key = GetTitleKey(title);
            if (key.Length >= 4 && _knownGameTitles.Count < 10_000)
                _knownGameTitles.TryAdd(key, title);
        }

        public async Task<VersionSelectionResult> FindAndSelectBestVersionAsync(
            string gameTitle,
            string targetPath,
            CancellationToken ct = default)
        {
            _logger.LogInformation("Finding and selecting best version for '{GameTitle}'...", gameTitle);

            // Search across sources with error tracking
            var (allCandidates, sourceErrors) = await SearchAllSourcesWithErrorsAsync(gameTitle, ct);

            // Filter candidates that match this game title using title normalizer
            var matchingQueries = GameSearchQueryMatcher.GetQueryAlternatives(
                    gameTitle, _knownGameTitles.Values)
                .Prepend(gameTitle)
                .ToArray();
            var matchedCandidates = allCandidates
                .Where(candidate => !GameReleaseMatcher.IsConsoleRelease(candidate)
                    && matchingQueries.Any(query => GameReleaseMatcher.IsForGame(candidate, query)))
                .ToList();

            // Never fall back to loosely matched results: that can put another numbered
            // game in the series (e.g. Black Ops II/VI) on the current game's card.
            var result = _versionSelector.SelectBestVersion(matchedCandidates, targetPath);
            result.Errors.AddRange(sourceErrors);
            _logger.LogInformation("Selection result for '{GameTitle}': {Summary}", gameTitle, result.StrategyExplanation);

            return result;
        }

        public VersionSelectionResult EvaluateCandidate(GameReleaseCandidate candidate, string targetPath)
            => _versionSelector.SelectBestVersion(new[] { candidate }, targetPath);

        public async Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(CancellationToken ct = default)
        {
            var allResults = new ConcurrentBag<GameReleaseCandidate>();
            var activeSources = _sources.Where(s => s.IsEnabled).ToList();

            if (activeSources.Count == 0)
                return Array.Empty<GameReleaseCandidate>();

            var tasks = activeSources.Select(async source =>
            {
                try
                {
                    using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sourceTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var results = await source.GetLatestGamesAsync(sourceTimeout.Token);
                    foreach (var item in results)
                    {
                        allResults.Add(item);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Source {SourceId} GetLatestGamesAsync failed", source.SourceId);
                }
            });

            await Task.WhenAll(tasks);

            // Keep one release per title per source so source filters can show
            // results from every tracker. The main "All" view deduplicates titles.
            var allCandidates = allResults
                .Where(candidate => !GameReleaseMatcher.IsConsoleRelease(candidate))
                .ToList();
            RememberGameTitles(allCandidates);
            var grouped = allCandidates
                .GroupBy(r => $"{r.SourceId}|{GetTitleKey(!string.IsNullOrWhiteSpace(r.CleanTitle) ? r.CleanTitle : r.RawTitle)}")
                .Select(g => g.OrderBy(c => c.SourcePriority)
                    .ThenBy(c => c.VersionType == GameVersionType.Portable ? 0 : 1)
                    .ThenByDescending(c => c.Leechers)
                    .ThenByDescending(c => c.Seeders)
                    .ThenByDescending(c => c.PublishDate ?? DateTime.MinValue)
                    .First())
                .OrderByDescending(c => c.Leechers)
                .ThenByDescending(c => c.Seeders)
                .ThenByDescending(c => c.PublishDate ?? DateTime.MinValue)
                .ThenBy(c => c.CleanTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CacheLatestGames(grouped);
            return grouped;
        }

        public IReadOnlyList<GameReleaseCandidate> GetCachedLatestGames()
        {
            try
            {
                lock (LatestCacheLock)
                {
                    if (!File.Exists(LatestCacheFile))
                        return Array.Empty<GameReleaseCandidate>();

                    var cached = JsonSerializer.Deserialize<List<GameReleaseCandidate>>(
                        File.ReadAllText(LatestCacheFile), LatestCacheJson);
                    if (cached == null || cached.Count == 0)
                        return Array.Empty<GameReleaseCandidate>();

                    var activeSources = _sources
                        .Where(source => source.IsEnabled)
                        .Select(source => source.SourceId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    return cached
                        .Where(candidate => activeSources.Contains(candidate.SourceId))
                        .Where(candidate => !GameReleaseMatcher.IsConsoleRelease(candidate))
                        .Take(60)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the cached latest releases; waiting for the network instead.");
                return Array.Empty<GameReleaseCandidate>();
            }
        }

        private void CacheLatestGames(IReadOnlyList<GameReleaseCandidate> releases)
        {
            if (releases.Count == 0)
                return;

            try
            {
                lock (LatestCacheLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LatestCacheFile)!);
                    var temporary = LatestCacheFile + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(releases.Take(60).ToList(), LatestCacheJson));
                    File.Move(temporary, LatestCacheFile, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not cache the latest releases.");
            }
        }

        private static string GetTitleKey(string title)
            => new(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
