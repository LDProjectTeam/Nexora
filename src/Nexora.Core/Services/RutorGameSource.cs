using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Game source that searches rutor.info (and mirrors) for available game releases.
    /// Extracts release title, magnet link, download size, seeds/leeches, and categorizes version.
    /// </summary>
    public class RutorGameSource : IPagedGameSource
    {
        private const int ResultsPerPage = 100;
        private static readonly TimeSpan PositiveSearchCacheDuration = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan EmptySearchCacheDuration = TimeSpan.FromSeconds(15);
        private readonly ConcurrentDictionary<string, SearchCacheEntry> _searchCache = new(StringComparer.Ordinal);
        private readonly HttpClient _http;
        private readonly ILogger<RutorGameSource> _logger;
        private readonly ITitleNormalizer _normalizer;

        public string SourceId => "rutor";
        public string DisplayName => "Rutor";
        public int Priority { get; set; } = 1;
        public bool IsEnabled { get; set; } = true;
        public int PageSize => ResultsPerPage;

        private static readonly string[] DefaultMirrors = new[]
        {
            "https://rutor.info",
            "https://rutor.is",
            "https://mirror-rutor.org"
        };
        private static volatile string? _preferredMirror;

        public static string SanitizeSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            // Remove dots between single letters, e.g. "S.T.A.L.K.E.R." -> "STALKER"
            var cleaned = Regex.Replace(query.Trim(), @"(?<=\b\p{L})\.(?=\p{L}\b)", "");

            // Replace special characters that break Sphinx (like :, (), [], !, ', ", -, /, \) with space
            cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s]", " ");

            // Collapse multiple spaces
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return cleaned;
        }

        private static IEnumerable<string> GetOrderedMirrors()
        {
            var preferred = _preferredMirror;
            if (!string.IsNullOrEmpty(preferred))
                yield return preferred;

            foreach (var mirror in DefaultMirrors)
            {
                if (!string.Equals(mirror, preferred, StringComparison.OrdinalIgnoreCase))
                    yield return mirror;
            }
        }

        // В .NET Core / .NET 5+ для Windows-1251 нужен CodePagesEncodingProvider.
        static RutorGameSource()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public RutorGameSource(
            HttpClient http,
            ILogger<RutorGameSource> logger,
            ITitleNormalizer normalizer)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        }

        public async Task<IReadOnlyList<GameReleaseCandidate>> SearchAsync(
            string query,
            CancellationToken ct = default)
        {
            var results = new List<GameReleaseCandidate>();

            if (string.IsNullOrWhiteSpace(query))
                return results;

            var sanitizedQuery = SanitizeSearchQuery(query);
            if (string.IsNullOrWhiteSpace(sanitizedQuery))
                sanitizedQuery = query.Trim();

            var cacheKey = Regex.Replace(sanitizedQuery, @"\s+", " ").ToLowerInvariant();
            if (_searchCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.ExpiresAtUtc > DateTime.UtcNow)
                    return CloneCandidates(cached.Candidates);

                _searchCache.TryRemove(cacheKey, out _);
            }

            foreach (var mirror in GetOrderedMirrors())
            {
                try
                {
                    var searchUrl =
                        $"{mirror}/search/0/8/000/0/{Uri.EscapeDataString(sanitizedQuery)}";

                    _logger.LogInformation(
                        "Querying Rutor mirror: {Url}",
                        searchUrl);

                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        searchUrl);

                    request.Headers.Add(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/120.0.0.0 Safari/537.36");

                    using var response = await _http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);

                    if (!response.IsSuccessStatusCode)
                        continue;

                    var html = await ReadHtmlAsync(response.Content, ct);

                    var candidates = ParseRutorHtml(html, mirror);

                    _preferredMirror = mirror;

                    if (candidates.Count > 0)
                    {
                        _logger.LogInformation(
                            "Found {Count} releases from Rutor mirror {Mirror}",
                            candidates.Count,
                            mirror);
                        CacheSearchResults(cacheKey, candidates);
                        return candidates;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to query Rutor mirror {Mirror}",
                        mirror);
                }
            }

            ct.ThrowIfCancellationRequested();
            CacheSearchResults(cacheKey, results);
            return results;
        }

        public async Task<IReadOnlyList<GameReleaseCandidate>> GetGamesPageAsync(
            int page,
            GameBrowseOrder order,
            CancellationToken ct = default)
        {
            if (page < 0)
                throw new ArgumentOutOfRangeException(nameof(page));

            var sort = order switch
            {
                GameBrowseOrder.Alphabetical => 7,
                GameBrowseOrder.MostDownloaded => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(order))
            };

            foreach (var mirror in GetOrderedMirrors())
            {
                ct.ThrowIfCancellationRequested();
                var browseUrl = $"{mirror}/browse/{page}/8/0/{sort}";

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, browseUrl);
                    request.Headers.Add(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/120.0.0.0 Safari/537.36");

                    using var response = await _http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var html = await ReadHtmlAsync(response.Content, ct);
                    var results = ParseRutorHtml(html, mirror);
                    _preferredMirror = mirror;
                    if (results.Count > 0)
                        return results;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to fetch Rutor catalog page {Page} from {Mirror}",
                        page + 1,
                        mirror);
                }
            }

            return Array.Empty<GameReleaseCandidate>();
        }

        public async Task<IReadOnlyList<GameReleaseCandidate>> GetLatestGamesAsync(
            CancellationToken ct = default)
        {
            const int PageSize = 100;
            const int MaximumPages = 5;
            var today = DateTime.Today;
            var fromDate = today.AddDays(-6).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var toDate = today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var results = new List<GameReleaseCandidate>();
            var seenReleases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mirror in GetOrderedMirrors())
            {
                for (var page = 0; page < MaximumPages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    var browseUrl = $"{mirror}/browse/{page}/8/0/4;{fromDate};{toDate}";

                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, browseUrl);
                        request.Headers.Add(
                            "User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                            "AppleWebKit/537.36 (KHTML, like Gecko) " +
                            "Chrome/120.0.0.0 Safari/537.36");

                        using var response = await _http.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            ct);

                        if (!response.IsSuccessStatusCode)
                            break;

                        var html = await ReadHtmlAsync(response.Content, ct);
                        var pageResults = ParseRutorHtml(html, mirror);
                        _preferredMirror = mirror;

                        foreach (var candidate in pageResults)
                        {
                            if (candidate.PublishDate is not DateTime publishDate
                                || publishDate.Date < today.AddDays(-7)
                                || publishDate.Date > today)
                                continue;

                            var releaseKey = candidate.DetailsUrl
                                ?? $"{candidate.CleanTitle}|{publishDate:yyyy-MM-dd}";
                            if (seenReleases.Add(releaseKey))
                                results.Add(candidate);
                        }

                        _logger.LogDebug(
                            "Rutor trending page {Page} returned {Count} releases from the last week",
                            page + 1,
                            pageResults.Count);

                        if (pageResults.Count < PageSize)
                            break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to fetch Rutor trending page {Page} from {Mirror}",
                            page + 1,
                            mirror);
                        break;
                    }
                }

                if (results.Count > 0)
                    break;
            }

            return results
                .OrderByDescending(candidate => candidate.Leechers)
                .ThenByDescending(candidate => candidate.Seeders)
                .ThenByDescending(candidate => candidate.PublishDate)
                .ToList();
        }

        private void CacheSearchResults(string cacheKey, IReadOnlyList<GameReleaseCandidate> candidates)
        {
            var now = DateTime.UtcNow;
            if (_searchCache.Count >= 256)
            {
                foreach (var entry in _searchCache)
                {
                    if (entry.Value.ExpiresAtUtc <= now || _searchCache.Count >= 384)
                        _searchCache.TryRemove(entry.Key, out _);
                }
            }

            var lifetime = candidates.Count > 0
                ? PositiveSearchCacheDuration
                : EmptySearchCacheDuration;
            _searchCache[cacheKey] = new SearchCacheEntry(
                CloneCandidates(candidates), now.Add(lifetime));
        }

        private static List<GameReleaseCandidate> CloneCandidates(
            IReadOnlyList<GameReleaseCandidate> candidates)
            => candidates.Select(candidate => new GameReleaseCandidate
            {
                Id = candidate.Id,
                RawTitle = candidate.RawTitle,
                CleanTitle = candidate.CleanTitle,
                AltTitle = candidate.AltTitle,
                SourceId = candidate.SourceId,
                SourceName = candidate.SourceName,
                SourcePriority = candidate.SourcePriority,
                VersionType = candidate.VersionType,
                Version = candidate.Version,
                ReleaseAuthor = candidate.ReleaseAuthor,
                DownloadSizeBytes = candidate.DownloadSizeBytes,
                EstimatedInstalledSizeBytes = candidate.EstimatedInstalledSizeBytes,
                MagnetLink = candidate.MagnetLink,
                TorrentFileUrl = candidate.TorrentFileUrl,
                DirectUrl = candidate.DirectUrl,
                DetailsUrl = candidate.DetailsUrl,
                Seeders = candidate.Seeders,
                Leechers = candidate.Leechers,
                PublishDate = candidate.PublishDate,
                Language = candidate.Language,
                SteamAppId = candidate.SteamAppId
            }).ToList();

        private sealed record SearchCacheEntry(
            IReadOnlyList<GameReleaseCandidate> Candidates,
            DateTime ExpiresAtUtc);

        /// <summary>
        /// Reads HTML using the charset specified by Content-Type.
        /// If charset is missing or invalid, falls back to Windows-1251,
        /// which is commonly used by older Russian-language sites.
        /// </summary>
        private static async Task<string> ReadHtmlAsync(
            HttpContent content,
            CancellationToken ct = default)
        {
            var bytes = await content.ReadAsByteArrayAsync(ct);

            var charset = content.Headers.ContentType?.CharSet;

            Encoding encoding;

            if (!string.IsNullOrWhiteSpace(charset))
            {
                charset = charset.Trim().Trim('"', '\'');

                try
                {
                    encoding = Encoding.GetEncoding(charset);
                }
                catch (ArgumentException)
                {
                    // Неизвестная кодировка — используем cp1251.
                    encoding = Encoding.GetEncoding(1251);
                }
            }
            else
            {
                // Rutor и старые русскоязычные зеркала могут не указывать charset.
                encoding = Encoding.GetEncoding(1251);
            }

            return encoding.GetString(bytes);
        }

        private List<GameReleaseCandidate> ParseRutorHtml(
            string html,
            string mirrorRoot)
        {
            var list = new List<GameReleaseCandidate>();
            var doc = new HtmlDocument();

            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes(
                "//div[@id='index']//tr[td]");

            if (rows == null || rows.Count == 0)
                return list;

            foreach (var row in rows)
            {
                try
                {
                    var cells = row.SelectNodes("td");

                    if (cells == null || cells.Count < 3)
                        continue;

                    // The cell with the magnet link. Its index differs between rows:
                    // a row has an extra "comments" cell only when the release has comments.
                    HtmlNode? linkCell = null;
                    string? magnetLink = null;

                    foreach (var cell in cells)
                    {
                        var magnetNode = cell.SelectSingleNode(".//a[starts-with(@href, 'magnet:')]");
                        if (magnetNode == null)
                            continue;

                        linkCell = cell;
                        // href is HTML-encoded: "&amp;dn=..." must become "&dn=..." or the magnet breaks.
                        magnetLink = HtmlEntity.DeEntitize(magnetNode.GetAttributeValue("href", ""));
                        break;
                    }

                    if (linkCell == null || string.IsNullOrEmpty(magnetLink))
                        continue;

                    var titleNode = linkCell
                        .SelectSingleNode(
                            ".//a[starts-with(@href, '/torrent/')]");

                    var rawTitle = HtmlEntity.DeEntitize(titleNode?.InnerText?.Trim() ?? string.Empty);

                    if (string.IsNullOrEmpty(rawTitle))
                        continue;

                    var detailsUrl = titleNode != null
                        ? $"{mirrorRoot}{titleNode.GetAttributeValue("href", "")}"
                        : null;

                    string? torrentFileUrl = null;
                    var downloadNode = row.SelectSingleNode(".//a[contains(@href, '/download/')]");
                    if (downloadNode != null)
                    {
                        var dlHref = downloadNode.GetAttributeValue("href", "");
                        if (dlHref.StartsWith("//"))
                            torrentFileUrl = "http:" + dlHref;
                        else if (dlHref.StartsWith("/"))
                            torrentFileUrl = "http://d.rutor.info" + dlHref;
                        else if (dlHref.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            torrentFileUrl = dlHref;
                    }

                    if (string.IsNullOrEmpty(torrentFileUrl) && titleNode != null)
                    {
                        var idMatch = Regex.Match(titleNode.GetAttributeValue("href", ""), @"/torrent/(\d+)");
                        if (idMatch.Success)
                        {
                            torrentFileUrl = $"http://d.rutor.info/download/{idMatch.Groups[1].Value}";
                        }
                    }

                    // Size: the first other cell whose text looks like "12.3 GB".
                    long sizeBytes = 0;

                    foreach (var cell in cells)
                    {
                        if (ReferenceEquals(cell, linkCell))
                            continue;

                        var cellText = HtmlEntity.DeEntitize(cell.InnerText ?? string.Empty);

                        if (!Regex.IsMatch(cellText, @"\d\s*[KMGT]B", RegexOptions.IgnoreCase))
                            continue;

                        sizeBytes = ParseSize(cellText);

                        if (sizeBytes > 0)
                            break;
                    }

                    // Seeds / leeches: coloured spans anywhere in the row.
                    int seeders = ReadPeerCount(row, "green");
                    int leechers = ReadPeerCount(row, "red");

                    // Fallback: no coloured spans -> first two numbers of the last cell
                    // (only if that cell is not the size cell).
                    if (seeders == 0 && leechers == 0)
                    {
                        var lastCell = cells[cells.Count - 1];

                        if (!ReferenceEquals(lastCell, linkCell))
                        {
                            var peerText = HtmlEntity.DeEntitize(lastCell.InnerText ?? string.Empty);

                            if (!Regex.IsMatch(peerText, @"[KMGT]B", RegexOptions.IgnoreCase))
                            {
                                var numbers = Regex.Matches(peerText, @"\d+");

                                if (numbers.Count >= 2)
                                {
                                    int.TryParse(numbers[0].Value, out seeders);
                                    int.TryParse(numbers[1].Value, out leechers);
                                }
                            }
                        }
                    }

                    // Normalize metadata
                    var normalized = _normalizer.Normalize(rawTitle);

                    var candidate = new GameReleaseCandidate
                    {
                        RawTitle = rawTitle,

                        CleanTitle = !string.IsNullOrEmpty(normalized.CleanTitle)
                            ? normalized.CleanTitle
                            : rawTitle,

                        AltTitle = normalized.AltTitle,
                        SourceId = SourceId,
                        SourceName = DisplayName,
                        SourcePriority = Priority,
                        VersionType = normalized.VersionType,
                        Version = normalized.Version,
                        ReleaseAuthor = normalized.ReleaseAuthor,
                        DownloadSizeBytes = sizeBytes,

                        EstimatedInstalledSizeBytes =
                            normalized.VersionType == GameVersionType.Portable
                                ? sizeBytes
                                : (long)(sizeBytes * 1.5),

                        MagnetLink = magnetLink,
                        TorrentFileUrl = torrentFileUrl,
                        DetailsUrl = detailsUrl,
                        Seeders = seeders,
                        Leechers = leechers,

                        PublishDate =
                            ReleaseDateParser.Parse(row, titleNode)
                    };

                    list.Add(candidate);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Error parsing Rutor row");
                }
            }

            if (list.Count > 0)
                _logger.LogInformation(
                    "Rutor parsed {Count} releases. First: '{Title}' seeds={Seeds} leeches={Leeches} size={Size}",
                    list.Count, list[0].RawTitle, list[0].Seeders, list[0].Leechers, list[0].DownloadSizeBytes);

            return list;
        }

        /// <summary>Reads the number from the coloured seeds ("green") / leeches ("red") span of a row.</summary>
        private static int ReadPeerCount(HtmlNode row, string cssClass)
        {
            var node = row.SelectSingleNode($".//span[contains(@class, '{cssClass}')]");

            if (node == null)
                return 0;

            var match = Regex.Match(
                HtmlEntity.DeEntitize(node.InnerText ?? string.Empty),
                @"\d+");

            return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
        }

        private static long ParseSize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            // e.g. "12.35 GB" or "850.5 MB"
            var match = Regex.Match(
                text.Replace("&nbsp;", " "),
                @"([0-9]+(?:\.[0-9]+)?)\s*([KMGT]?B)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return 0;

            if (!double.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double val))
            {
                return 0;
            }

            var unit = match.Groups[2].Value.ToUpperInvariant();

            return unit switch
            {
                "TB" => (long)(val * 1024L * 1024 * 1024 * 1024),
                "GB" => (long)(val * 1024L * 1024 * 1024),
                "MB" => (long)(val * 1024L * 1024),
                "KB" => (long)(val * 1024L),
                _ => (long)val
            };
        }
    }
}
