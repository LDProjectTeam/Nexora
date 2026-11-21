using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Uses Steam's storefront top-sellers search to order featured games, then
    /// keeps only titles for which the configured Rutor source has a matching release.
    /// </summary>
    public sealed class SteamFeaturedGamesService : ISteamFeaturedGamesService
    {
        private const int SteamPageSize = 24;
        private const int MaxSteamPages = 5;
        private static readonly TimeSpan RutorSearchTimeout = TimeSpan.FromSeconds(10);

        private readonly HttpClient _httpClient;
        private readonly IGameSource _rutorSource;
        private readonly ITitleNormalizer _titleNormalizer;
        private readonly ILogger<SteamFeaturedGamesService> _logger;

        public SteamFeaturedGamesService(
            HttpClient httpClient,
            IEnumerable<IGameSource> gameSources,
            ITitleNormalizer titleNormalizer,
            ILogger<SteamFeaturedGamesService> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _rutorSource = gameSources?.FirstOrDefault(source =>
                    string.Equals(source.SourceId, "rutor", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Rutor game source is not registered.");
            _titleNormalizer = titleNormalizer ?? throw new ArgumentNullException(nameof(titleNormalizer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<SteamFeaturedGame>> GetFeaturedGamesAsync(
            int desiredCount = 10,
            CancellationToken cancellationToken = default,
            IProgress<IReadOnlyList<SteamFeaturedGame>>? progress = null)
        {
            if (desiredCount <= 0)
                return Array.Empty<SteamFeaturedGame>();
            desiredCount = Math.Min(desiredCount, 10);

            var featured = new List<SteamFeaturedGame>(desiredCount);
            var seenAppIds = new HashSet<uint>();
            var seenTitleKeys = new HashSet<string>(StringComparer.Ordinal);

            var start = 0;
            for (var page = 0; page < MaxSteamPages && featured.Count < desiredCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SteamSearchPage steamPage;
                try
                {
                    steamPage = await GetSteamPageAsync(start, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load Steam top-sellers page starting at {Start}", start);
                    break;
                }

                if (steamPage.Games.Count == 0)
                    break;

                var seenAppCountBeforePage = seenAppIds.Count;
                foreach (var steamGame in steamPage.Games)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenAppIds.Add(steamGame.AppId))
                        continue;

                    var titleKey = CreateTitleKey(steamGame.Name);
                    if (titleKey.Length == 0 || !seenTitleKeys.Add(titleKey))
                        continue;

                    IReadOnlyList<GameReleaseCandidate> releases;
                    using (var searchTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        searchTimeout.CancelAfter(RutorSearchTimeout);
                        try
                        {
                            releases = await _rutorSource.SearchAsync(steamGame.Name, searchTimeout.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("Rutor search timed out for Steam game {GameTitle}", steamGame.Name);
                            continue;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "Rutor search failed for Steam game {GameTitle}", steamGame.Name);
                            continue;
                        }
                    }

                    var matchingRelease = releases
                        .Where(release => IsMatchingRelease(release, steamGame.Name))
                        .OrderByDescending(release => release.Seeders > 0)
                        .ThenByDescending(release => release.VersionType == GameVersionType.Portable && release.Seeders >= 5)
                        .ThenByDescending(release => release.Seeders)
                        .ThenByDescending(release => release.Leechers)
                        .FirstOrDefault();
                    if (matchingRelease == null)
                        continue;

                    featured.Add(new SteamFeaturedGame(steamGame, matchingRelease));
                    progress?.Report(featured.ToArray());
                    if (featured.Count >= desiredCount)
                        break;
                }

                if (seenAppIds.Count == seenAppCountBeforePage)
                    break;

                start += steamPage.Games.Count;
                if (steamPage.TotalCount is int totalCount && start >= totalCount)
                    break;
            }

            return featured;
        }

        private async Task<SteamSearchPage> GetSteamPageAsync(int start, CancellationToken cancellationToken)
        {
            var url = $"https://store.steampowered.com/search/results/?filter=globaltopsellers" +
                $"&ignore_preferences=1&category1=998&json=1&start={start}&count={SteamPageSize}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            int? totalCount = root.TryGetProperty("total_count", out var countElement)
                && countElement.TryGetInt32(out var parsedCount)
                    ? parsedCount
                    : null;

            var games = ParseGames(root);
            if (games.Count == 0 && root.TryGetProperty("results_html", out var htmlElement)
                && htmlElement.ValueKind == JsonValueKind.String)
            {
                games = ParseHtmlGames(htmlElement.GetString() ?? string.Empty);
            }

            if (games.Count == 0)
                throw new InvalidDataException("Steam response did not contain supported game results.");

            return new SteamSearchPage(games, totalCount);
        }

        private static IReadOnlyList<SteamPopularGame> ParseGames(JsonElement root)
        {
            var parsedGames = new List<SteamPopularGame>();
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return parsedGames;

            foreach (var item in items.EnumerateArray())
            {
                var title = ReadString(item, "name");
                var logoUrl = ReadString(item, "logo");
                var appIdText = ReadString(item, "appid");
                if (appIdText.Length == 0)
                    appIdText = ReadString(item, "app_id");
                if (appIdText.Length == 0)
                    appIdText = ReadString(item, "id");

                if (!uint.TryParse(appIdText, out var appId))
                {
                    var logoMatch = Regex.Match(logoUrl, @"/apps/(?<appid>\d+)/", RegexOptions.IgnoreCase);
                    if (!logoMatch.Success || !uint.TryParse(logoMatch.Groups["appid"].Value, out appId))
                        continue;
                }

                if (!string.IsNullOrWhiteSpace(title))
                    parsedGames.Add(new SteamPopularGame(appId, title.Trim(), logoUrl));
            }

            return parsedGames;
        }

        private static string ReadString(JsonElement item, string propertyName)
        {
            if (!item.TryGetProperty(propertyName, out var value))
                return string.Empty;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty
            };
        }

        private static IReadOnlyList<SteamPopularGame> ParseHtmlGames(string html)
        {
            var parsedGames = new List<SteamPopularGame>();
            var document = new HtmlDocument();
            document.LoadHtml(html);

            var resultNodes = document.DocumentNode.SelectNodes(
                "//a[contains(concat(' ', normalize-space(@class), ' '), ' search_result ')]");
            if (resultNodes == null)
                return parsedGames;

            foreach (var resultNode in resultNodes)
            {
                var appIdValue = resultNode.GetAttributeValue("data-ds-appid", string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (!uint.TryParse(appIdValue, out var appId))
                    continue;

                var titleNode = resultNode.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' title ')]");
                var title = HtmlEntity.DeEntitize(titleNode?.InnerText ?? string.Empty).Trim();
                if (title.Length == 0)
                    continue;

                parsedGames.Add(new SteamPopularGame(appId, title));
            }

            return parsedGames;
        }

        private bool IsMatchingRelease(GameReleaseCandidate release, string steamTitle)
        {
            var cleanTitle = !string.IsNullOrWhiteSpace(release.CleanTitle)
                ? release.CleanTitle
                : _titleNormalizer.Normalize(release.RawTitle).CleanTitle;
            var alternateTitle = !string.IsNullOrWhiteSpace(release.AltTitle)
                ? release.AltTitle
                : _titleNormalizer.Normalize(release.RawTitle).AltTitle;

            return _titleNormalizer.AreTitlesMatching(cleanTitle, steamTitle)
                || _titleNormalizer.AreTitlesMatching(alternateTitle, steamTitle);
        }

        private static string CreateTitleKey(string title)
            => new(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private sealed record SteamSearchPage(IReadOnlyList<SteamPopularGame> Games, int? TotalCount);
    }
}
