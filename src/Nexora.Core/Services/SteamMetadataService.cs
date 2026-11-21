using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Resolves a game to its official Steam Store page and returns localized
    /// metadata. Results are cached per AppID + language, verified against the
    /// requested title and release year, and never allowed to block the caller
    /// for longer than the configured request timeout.
    /// </summary>
    public sealed class SteamMetadataService : ISteamMetadataService
    {
        private static readonly TimeSpan PositiveCacheLifetime = TimeSpan.FromDays(14);
        private static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromDays(7);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(9);
        private const int MaxAttempts = 3;
        private const double MinimumMatchScore = 0.66;

        private readonly HttpClient _http;
        private readonly string _cacheDirectory;
        private readonly ConcurrentDictionary<string, Lazy<Task<SteamMetadataLookupResult>>> _inFlight = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SteamMetadataCacheEntry> _memory = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _requestGate = new(3, 3);

        public SteamMetadataService()
            : this(new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(8)
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            })
        {
        }

        public SteamMetadataService(HttpClient httpClient)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _cacheDirectory = Path.Combine(PathHelper.AppDataDir, "steam-metadata");
        }

        public Task<SteamMetadataLookupResult> GetMetadataAsync(
            SteamMetadataRequest request,
            CancellationToken cancellationToken = default)
            => GetMetadataAsync(request, forceRefresh: false, cancellationToken);

        public async Task<SteamMetadataLookupResult> GetMetadataAsync(
            SteamMetadataRequest request,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var normalizedRequest = NormalizeRequest(request);
            var key = BuildCacheKey(normalizedRequest);

            if (!forceRefresh
                && _memory.TryGetValue(key, out var memoryEntry)
                && !IsExpired(memoryEntry))
            {
                return MarkFromCache(memoryEntry.Result);
            }

            // One network lookup per game/language at a time: opening several cards
            // for the same game must not send duplicate requests.
            var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<SteamMetadataLookupResult>>(
                () => LoadAsync(normalizedRequest, key, forceRefresh)));

            var task = lazy.Value;
            if (cancellationToken.CanBeCanceled)
            {
                // The shared task is intentionally not linked to a single caller:
                // closing a window should stop waiting without wasting the request.
                return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await task.ConfigureAwait(false);
        }

        private async Task<SteamMetadataLookupResult> LoadAsync(
            SteamMetadataRequest request,
            string key,
            bool forceRefresh)
        {
            try
            {
                var cached = await ReadCacheAsync(key).ConfigureAwait(false);
                if (!forceRefresh && cached != null && !IsExpired(cached))
                {
                    _memory[key] = cached;
                    return MarkFromCache(cached.Result);
                }

                try
                {
                    var result = await ResolveAsync(request).ConfigureAwait(false);
                    var entry = new SteamMetadataCacheEntry
                    {
                        Result = result,
                        FetchedAtUtc = DateTime.UtcNow
                    };
                    _memory[key] = entry;
                    await WriteCacheAsync(key, entry).ConfigureAwait(false);
                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (cached != null)
                    {
                        // Offline or Steam unavailable: show the last known good data.
                        var stale = MarkFromCache(cached.Result);
                        stale.Error = ex.Message;
                        return stale;
                    }

                    return new SteamMetadataLookupResult { Error = ex.Message };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new SteamMetadataLookupResult { Error = ex.Message };
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }

        private async Task<SteamMetadataLookupResult> ResolveAsync(SteamMetadataRequest request)
        {
            var language = NormalizeLanguage(request.LanguageCode);
            var requestYear = ResolveReleaseYear(request);

            if (request.AppId is int explicitId && explicitId > 0)
            {
                var explicitMetadata = await FetchAppDetailsAsync(explicitId, language).ConfigureAwait(false);
                if (explicitMetadata != null
                    && IsGameType(explicitMetadata.Type)
                    && IsTitleCompatible(request, explicitMetadata.Name)
                    && IsYearCompatible(requestYear, explicitMetadata.ReleaseYear))
                {
                    await EnsureDescriptionFallbackAsync(explicitMetadata, language).ConfigureAwait(false);
                    return Success(explicitMetadata);
                }
            }

            var searchCandidates = await SearchAsync(request).ConfigureAwait(false);
            if (searchCandidates.Count == 0)
            {
                return new SteamMetadataLookupResult { IsNotFound = true };
            }

            var valid = new List<(StoreSearchCandidate Candidate, SteamGameMetadata Metadata)>();
            foreach (var candidate in searchCandidates)
            {
                var metadata = await FetchAppDetailsAsync(candidate.Id, language).ConfigureAwait(false);
                if (metadata == null)
                    continue;
                if (!IsGameType(metadata.Type))
                    continue;
                if (!IsTitleCompatible(request, metadata.Name))
                    continue;
                if (!ArtworkTitleMatcher.IsCompatible(candidate.Name, metadata.Name))
                    continue;
                if (!IsYearCompatible(requestYear, metadata.ReleaseYear))
                    continue;

                valid.Add((candidate, metadata));
                if (valid.Count >= 2)
                    break;
            }

            if (valid.Count == 0)
            {
                // Store results existed but none of them could be verified: never
                // show metadata from a game we are not sure about.
                return new SteamMetadataLookupResult { IsAmbiguous = true };
            }

            var ordered = valid.OrderByDescending(v => v.Candidate.Score).ToList();
            if (ordered.Count > 1 && ordered[0].Candidate.Score - ordered[1].Candidate.Score < 0.05)
            {
                return new SteamMetadataLookupResult { IsAmbiguous = true };
            }

            var best = ordered[0].Metadata;
            await EnsureDescriptionFallbackAsync(best, language).ConfigureAwait(false);
            return Success(best);
        }

        private async Task<List<StoreSearchCandidate>> SearchAsync(SteamMetadataRequest request)
        {
            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Title))
                titles.Add(request.Title);
            if (!string.IsNullOrWhiteSpace(request.AlternateTitle)
                && !titles.Contains(request.AlternateTitle, StringComparer.OrdinalIgnoreCase))
                titles.Add(request.AlternateTitle);

            var results = new List<StoreSearchCandidate>();
            foreach (var title in titles)
            {
                foreach (var query in BuildSearchQueries(title).Take(3))
                {
                    var items = await SearchStoreAsync(query).ConfigureAwait(false);
                    foreach (var item in items)
                    {
                        if (item.Id <= 0 || !IsGameType(NormalizeStoreType(item.Type)))
                            continue;
                        if (!ArtworkTitleMatcher.IsCompatible(title, item.Name))
                            continue;

                        var score = Math.Max(MatchScore(query, item.Name), MatchScore(title, item.Name));
                        if (score < MinimumMatchScore)
                            continue;

                        results.Add(new StoreSearchCandidate(item.Id, item.Name, score));
                    }

                    if (results.Count > 0)
                        break;
                }

                if (results.Count > 0)
                    break;
            }

            return results
                .GroupBy(c => c.Id)
                .Select(group => group.OrderByDescending(c => c.Score).First())
                .OrderByDescending(c => c.Score)
                .Take(4)
                .ToList();
        }

        private async Task<SteamGameMetadata?> FetchAppDetailsAsync(int appId, string language)
        {
            if (appId <= 0)
                return null;

            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={language}&cc=US";
            var json = await GetStringAsync(url).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var root)
                    || !root.TryGetProperty("success", out var success)
                    || success.ValueKind != JsonValueKind.True
                    || !root.TryGetProperty("data", out var data))
                {
                    return null;
                }

                var metadata = new SteamGameMetadata
                {
                    AppId = appId,
                    Name = ReadString(data, "name"),
                    Type = ReadString(data, "type"),
                    ShortDescription = ReadString(data, "short_description"),
                    DetailedDescription = ReadString(data, "detailed_description"),
                    Developers = ReadStringArray(data, "developers"),
                    Publishers = ReadStringArray(data, "publishers"),
                    Genres = ReadGenres(data),
                    Platforms = ReadPlatforms(data),
                    HeaderImageUrl = ReadString(data, "header_image"),
                    Website = ReadString(data, "website"),
                    SupportedLanguages = StripHtml(ReadString(data, "supported_languages")),
                    MinimumRequirements = StripHtml(ReadNestedString(data, "pc_requirements", "minimum")),
                    RecommendedRequirements = StripHtml(ReadNestedString(data, "pc_requirements", "recommended")),
                    FetchedAtUtc = DateTime.UtcNow,
                    LanguageCode = language
                };

                metadata.BackgroundImageUrl = ReadString(data, "background_raw");
                if (string.IsNullOrWhiteSpace(metadata.BackgroundImageUrl))
                    metadata.BackgroundImageUrl = ReadString(data, "background");

                if (data.TryGetProperty("release_date", out var releaseDate))
                {
                    metadata.ReleaseDateText = ReadString(releaseDate, "date");
                    metadata.ComingSoon = releaseDate.TryGetProperty("coming_soon", out var comingSoon)
                        && comingSoon.ValueKind == JsonValueKind.True;
                }
                metadata.ReleaseYear = ParseYear(metadata.ReleaseDateText);

                if (data.TryGetProperty("metacritic", out var metacritic))
                {
                    metadata.MetacriticScore = metacritic.TryGetProperty("score", out var score)
                        ? score.ValueKind == JsonValueKind.Number ? score.GetInt32().ToString(CultureInfo.InvariantCulture) : ReadString(metacritic, "score")
                        : string.Empty;
                }

                metadata.StoreUrl = $"https://store.steampowered.com/app/{appId}/";
                return metadata;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private async Task EnsureDescriptionFallbackAsync(SteamGameMetadata metadata, string language)
        {
            if (string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
                return;
            if (metadata.HasDescription)
                return;

            var english = await FetchAppDetailsAsync(metadata.AppId, "english").ConfigureAwait(false);
            if (english == null)
                return;

            metadata.IsEnglishDescriptionFallback = true;

            if (string.IsNullOrWhiteSpace(metadata.ShortDescription))
                metadata.ShortDescription = english.ShortDescription;
            if (string.IsNullOrWhiteSpace(metadata.DetailedDescription))
                metadata.DetailedDescription = english.DetailedDescription;
            if (string.IsNullOrWhiteSpace(metadata.SupportedLanguages))
                metadata.SupportedLanguages = english.SupportedLanguages;
            if (string.IsNullOrWhiteSpace(metadata.MinimumRequirements))
                metadata.MinimumRequirements = english.MinimumRequirements;
            if (string.IsNullOrWhiteSpace(metadata.RecommendedRequirements))
                metadata.RecommendedRequirements = english.RecommendedRequirements;
        }

        private async Task<IReadOnlyList<StoreSearchItem>> SearchStoreAsync(string query)
        {
            var url = "https://store.steampowered.com/api/storesearch/?term="
                      + Uri.EscapeDataString(query) + "&l=english&cc=US";
            var json = await GetStringAsync(url).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<StoreSearchItem>();

            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<StoreSearchItem>();
                }

                var list = new List<StoreSearchItem>();
                foreach (var item in items.EnumerateArray())
                {
                    list.Add(new StoreSearchItem(
                        ReadInt(item, "id"),
                        ReadString(item, "name"),
                        ReadString(item, "type")));
                }
                return list;
            }
            catch (JsonException)
            {
                return Array.Empty<StoreSearchItem>();
            }
        }

        private async Task<string?> GetStringAsync(string url)
        {
            await _requestGate.WaitAsync().ConfigureAwait(false);
            try
            {
                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    using var timeout = new CancellationTokenSource(RequestTimeout);
                    try
                    {
                        using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            var retriable = response.StatusCode == HttpStatusCode.TooManyRequests
                                            || (int)response.StatusCode >= 500;
                            if (!retriable || attempt >= MaxAttempts - 1)
                                return null;

                            await Task.Delay(500 * (attempt + 1), timeout.Token).ConfigureAwait(false);
                            continue;
                        }

                        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (attempt < MaxAttempts - 1)
                    {
                        // Timeout: retry with a fresh budget.
                    }
                    catch (HttpRequestException) when (attempt < MaxAttempts - 1)
                    {
                        await Task.Delay(500 * (attempt + 1)).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _requestGate.Release();
            }

            return null;
        }

        private static SteamMetadataRequest NormalizeRequest(SteamMetadataRequest request)
        {
            var language = NormalizeLanguage(request.LanguageCode);
            return new SteamMetadataRequest
            {
                Title = (request.Title ?? string.Empty).Trim(),
                AlternateTitle = request.AlternateTitle?.Trim(),
                RawTitle = request.RawTitle?.Trim(),
                AppId = request.AppId,
                ReleaseYear = request.ReleaseYear,
                LanguageCode = language
            };
        }

        private static string BuildCacheKey(SteamMetadataRequest request)
        {
            var raw = string.Join("|", new[]
            {
                NormalizeForKey(request.Title),
                NormalizeForKey(request.AlternateTitle ?? string.Empty),
                request.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                request.AppId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                request.LanguageCode
            });
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool IsExpired(SteamMetadataCacheEntry entry)
        {
            var lifetime = entry.Result.IsAvailable ? PositiveCacheLifetime : NegativeCacheLifetime;
            return DateTime.UtcNow - entry.FetchedAtUtc > lifetime;
        }

        private static SteamMetadataLookupResult MarkFromCache(SteamMetadataLookupResult result)
        {
            return new SteamMetadataLookupResult
            {
                Metadata = result.Metadata,
                IsFromCache = true,
                IsAmbiguous = result.IsAmbiguous,
                IsNotFound = result.IsNotFound,
                Error = result.Error
            };
        }

        private async Task<SteamMetadataCacheEntry?> ReadCacheAsync(string key)
        {
            if (_memory.TryGetValue(key, out var cached))
                return cached;

            try
            {
                var path = Path.Combine(_cacheDirectory, key + ".json");
                if (!File.Exists(path))
                    return null;

                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                var entry = JsonSerializer.Deserialize<SteamMetadataCacheEntry>(json);
                if (entry?.Result == null)
                    return null;

                _memory[key] = entry;
                return entry;
            }
            catch
            {
                return null;
            }
        }

        private async Task WriteCacheAsync(string key, SteamMetadataCacheEntry entry)
        {
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                var path = Path.Combine(_cacheDirectory, key + ".json");
                var json = JsonSerializer.Serialize(entry);
                await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            }
            catch
            {
                // A failed cache write must never break the details window.
            }
        }

        private static SteamMetadataLookupResult Success(SteamGameMetadata metadata)
            => new() { Metadata = metadata };

        private static bool IsTitleCompatible(SteamMetadataRequest request, string storeName)
        {
            if (string.IsNullOrWhiteSpace(storeName))
                return false;

            if (ArtworkTitleMatcher.IsCompatible(request.Title, storeName))
                return true;

            return !string.IsNullOrWhiteSpace(request.AlternateTitle)
                   && ArtworkTitleMatcher.IsCompatible(request.AlternateTitle, storeName);
        }

        private static bool IsYearCompatible(int? requestedYear, int? storeYear)
        {
            if (requestedYear is null || storeYear is null)
                return true;

            // A local release may slip into the next calendar year; anything larger
            // usually means a remake, remaster or a different entry in the series.
            return Math.Abs(requestedYear.Value - storeYear.Value) <= 1;
        }

        private static int? ResolveReleaseYear(SteamMetadataRequest request)
        {
            var explicitYear = request.ReleaseYear;
            if (explicitYear is int year && IsPlausibleYear(year))
                return year;

            foreach (var text in new[] { request.RawTitle, request.Title, request.AlternateTitle })
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // Only parenthesized years are reliable: "Metro 2033" and
                // "Cyberpunk 2077" contain numbers that are part of the title.
                var match = Regex.Match(text, @"\(((?:19[5-9]\d|20\d\d))\)");
                if (match.Success
                    && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    && IsPlausibleYear(parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        private static bool IsPlausibleYear(int year)
            => year >= 1970 && year <= DateTime.UtcNow.Year + 1;

        private static int? ParseYear(string? dateText)
        {
            if (string.IsNullOrWhiteSpace(dateText))
                return null;

            var match = Regex.Match(dateText, @"\b(19[5-9]\d|20\d\d)\b");
            return match.Success
                   && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                ? year
                : null;
        }

        private static bool IsGameType(string? type)
            => string.Equals(type, "game", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeStoreType(string? type)
            => string.Equals(type, "app", StringComparison.OrdinalIgnoreCase) ? "game" : (type ?? string.Empty);

        private static string NormalizeLanguage(string? language)
        {
            return language?.Trim().ToLowerInvariant() switch
            {
                "ru" or "russian" => "russian",
                "uk" or "ua" or "ukrainian" => "ukrainian",
                _ => "english"
            };
        }

        private static string[] BuildSearchQueries(string title)
        {
            var input = title.Split('|')[0];
            input = Regex.Replace(input, @"\[[^\]]*\]|\([^\)]*\)", " ");
            input = Regex.Replace(
                input,
                @"\b(?:v\s*\d[\w.]*|repack|repacked|fitgirl|dodi|rutor|torrent)\b.*$",
                " ",
                RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\s*(?:\||\bpc\b|\bportable\b|\bgog\b|\bscene\b)\s*$", " ", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\s+", " ").Trim(' ', '-', ':', '|');

            var withoutEdition = Regex.Replace(
                input,
                @"(?:\s*[-:|]?\s*)(?:director'?s? cut|complete(?: the set)?|ultimate|definitive|maximum|deluxe|gold|premium|reloaded|enhanced|remastered|digital|standard|anniversary|collector'?s?|edition|bundle)(?:\s+edition)?\s*$",
                string.Empty,
                RegexOptions.IgnoreCase).Trim(' ', '-', ':', '|');

            var firstSegment = Regex.Split(input, @"\s+-\s+")[0].Trim(' ', '-', ':', '|');

            return new[] { input, firstSegment, withoutEdition }
                .Where(query => query.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static double MatchScore(string query, string candidate)
        {
            var queryTokens = TokenizeForMatch(query);
            var candidateTokens = TokenizeForMatch(candidate);
            if (queryTokens.Count == 0 || candidateTokens.Count == 0)
                return 0.0;

            var queryNumbers = ExtractNumericTokens(queryTokens);
            var candidateNumbers = ExtractNumericTokens(candidateTokens);
            if (queryNumbers.Count > 0 && candidateNumbers.Count > 0)
            {
                if (!queryNumbers.Overlaps(candidateNumbers))
                    return 0.0;
            }
            else if (queryNumbers.Count != candidateNumbers.Count)
            {
                return 0.0;
            }

            if (queryTokens.SetEquals(candidateTokens))
                return 1.0;

            var primaryToken = GetPrimaryToken(query);
            if (!string.IsNullOrEmpty(primaryToken) && !candidateTokens.Contains(primaryToken))
                return 0.0;

            var queryText = string.Join(' ', queryTokens.OrderBy(t => t, StringComparer.Ordinal));
            var candidateText = string.Join(' ', candidateTokens.OrderBy(t => t, StringComparer.Ordinal));
            if (candidateText.Contains(queryText, StringComparison.Ordinal))
                return 0.9;
            if (queryText.Contains(candidateText, StringComparison.Ordinal) && candidateTokens.Count >= 2)
                return 0.85;

            return (double)queryTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count()
                   / Math.Max(queryTokens.Count, candidateTokens.Count);
        }

        private static HashSet<string> ExtractNumericTokens(HashSet<string> words)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var word in words)
            {
                if (int.TryParse(word, out var value) && value >= 1 && value <= 99)
                    result.Add(word);
            }
            return result;
        }

        private static string GetPrimaryToken(string title)
        {
            var ignored = GetIgnoredTokens();
            return Regex.Matches(Normalize(title), @"[\p{L}\p{N}]+")
                .Select(match => match.Value)
                .FirstOrDefault(token => token.Length >= 3 && !ignored.Contains(token)) ?? string.Empty;
        }

        private static HashSet<string> TokenizeForMatch(string title)
        {
            var ignored = GetIgnoredTokens();
            return Regex.Matches(Normalize(title), @"[\p{L}\p{N}]+")
                .Select(match => NormalizeRomanNumeral(match.Value))
                .Where(token => !ignored.Contains(token))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string NormalizeRomanNumeral(string token) => token switch
        {
            "i" => "1",
            "ii" => "2",
            "iii" => "3",
            "iv" => "4",
            "v" => "5",
            "vi" => "6",
            "vii" => "7",
            "viii" => "8",
            "ix" => "9",
            "x" => "10",
            _ => token
        };

        private static string Normalize(string value)
            => string.Join(' ', Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value));

        private static string NormalizeForKey(string value)
            => string.Join(' ', Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value));

        private static HashSet<string> GetIgnoredTokens() => new(StringComparer.Ordinal)
        {
            "the", "a", "an", "and", "of", "in", "on", "for", "to", "with",
            "by", "at", "from", "game", "year", "goty", "edition", "bundle", "complete", "set",
            "ultimate", "definitive", "praetor", "centurion", "maximum", "deluxe", "gold", "premium",
            "reloaded", "enhanced", "remastered", "digital", "standard", "anniversary", "collector",
            "collectors", "repack", "portable", "rip", "directors", "director", "cut", "patch", "update",
            "v", "build", "multi", "rus", "eng", "steamrip", "dodi", "fitgirl", "gog", "pc", "all",
            "dlc", "dlcs", "soundtrack", "artbook", "pass", "season", "expansions", "expansion",
            "sid", "meier", "meiers", "tom", "clancy", "clancys", "marvel", "marvels"
        };

        private static string StripHtml(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            var text = Regex.Replace(html, @"<br\s*/?>|</p>|</li>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @"\n[ \t]+", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }

        private static string ReadString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string ReadNestedString(JsonElement element, string property, string nestedProperty)
        {
            if (!element.TryGetProperty(property, out var nested) || nested.ValueKind != JsonValueKind.Object)
                return string.Empty;
            return ReadString(nested, nestedProperty);
        }

        private static int ReadInt(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            return value.ValueKind == JsonValueKind.String
                   && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static List<string> ReadStringArray(JsonElement element, string property)
        {
            var list = new List<string>();
            if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add(text.Trim());
                }
            }
            return list;
        }

        private static List<string> ReadGenres(JsonElement element)
        {
            var list = new List<string>();
            if (!element.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var genre in genres.EnumerateArray())
            {
                var description = ReadString(genre, "description");
                if (!string.IsNullOrWhiteSpace(description)
                    && !list.Contains(description, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(description);
                }
            }
            return list;
        }

        private static List<string> ReadPlatforms(JsonElement element)
        {
            var list = new List<string>();
            if (!element.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Object)
                return list;

            foreach (var property in platforms.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.True)
                    list.Add(property.Name);
            }
            return list;
        }

        private sealed record StoreSearchItem(int Id, string Name, string Type);

        private sealed record StoreSearchCandidate(int Id, string Name, double Score);
    }

    internal sealed class SteamMetadataCacheEntry
    {
        public SteamMetadataLookupResult Result { get; set; } = new();

        public DateTime FetchedAtUtc { get; set; }
    }
}
