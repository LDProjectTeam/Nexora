using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using HtmlAgilityPack;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

#pragma warning disable CS8600, CS8601, CS8619, CS8620, CS8624, CS8625

namespace Vellum.Core.Services;

public sealed class SteamArtworkService : IGameArtworkService
{
	private sealed record StoreSearchItem(int Id, string Name, string TinyImage, string Type);

	private sealed record StoreSearchMatch(int Id, string Name, string TinyImage, string Type, double Score);

	private readonly HttpClient _httpClient;

	private readonly string _cacheDirectory = PathHelper.ArtworkCacheDir;

	private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(24, 24);

	private readonly SemaphoreSlim _backgroundGate = new SemaphoreSlim(6, 6);

	private readonly SemaphoreSlim _steamSearchGate = new SemaphoreSlim(8, 8);

	private readonly SemaphoreSlim _bingGate = new SemaphoreSlim(4, 4);

	private readonly ConcurrentDictionary<string, Lazy<Task<GameArtwork?>>> _inFlight = new ConcurrentDictionary<string, Lazy<Task<GameArtwork>>>(StringComparer.OrdinalIgnoreCase);

	// Titles resolved once keep their Steam app id, so later launches skip the store
	// search entirely and go straight to the CDN assets.
	private sealed record IndexedAppId(int AppId, string Name);

	private readonly ConcurrentDictionary<string, IndexedAppId> _appIdIndex = new ConcurrentDictionary<string, IndexedAppId>(StringComparer.OrdinalIgnoreCase);

	private readonly SemaphoreSlim _indexGate = new SemaphoreSlim(1, 1);

	private volatile bool _appIdIndexLoaded;

	private string AppIdIndexPath => Path.Combine(_cacheDirectory, "steam_appids.json");

	public SteamArtworkService(HttpClient httpClient)
	{
		_httpClient = httpClient;
		if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
		{
			_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Vellum/1.0");
		}
	}

	public Task<GameArtwork?> FindArtworkAsync(string gameTitle, CancellationToken cancellationToken = default(CancellationToken))
	{
		return FindArtworkAsync(gameTitle, null, ArtworkPriority.Background, false, cancellationToken);
	}

	public Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, CancellationToken cancellationToken = default(CancellationToken))
	{
		return FindArtworkAsync(gameTitle, releasePageUrl, ArtworkPriority.Background, false, cancellationToken);
	}

	public Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, ArtworkPriority priority, CancellationToken cancellationToken = default(CancellationToken))
	{
		return FindArtworkAsync(gameTitle, releasePageUrl, priority, false, cancellationToken);
	}

	public Task<GameArtwork?> FindArtworkAsync(string gameTitle, string? releasePageUrl, ArtworkPriority priority, bool forceRefresh, CancellationToken cancellationToken = default(CancellationToken))
	{
		string text = Normalize(gameTitle);
		if (text.Length < 2)
		{
			return Task.FromResult<GameArtwork>(null);
		}
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
		string key = hash + "|" + (releasePageUrl ?? string.Empty);
		if (forceRefresh || priority == ArtworkPriority.ModalDetails)
		{
			_inFlight.TryRemove(key, out _);
		}
		Lazy<Task<GameArtwork>> orAdd = _inFlight.GetOrAdd(key, (string _) => new Lazy<Task<GameArtwork>>(() => FindAndCacheAsync(gameTitle, releasePageUrl, hash, priority, forceRefresh, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
		return AwaitAndRemoveOnCompletionAsync(key, orAdd);
	}

	private async Task<GameArtwork?> AwaitAndRemoveOnCompletionAsync(string key, Lazy<Task<GameArtwork?>> lazy)
	{
		try
		{
			return await lazy.Value.ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			_inFlight.TryRemove(key, out Lazy<Task<GameArtwork>> _);
		}
	}

	private async Task<GameArtwork?> FindAndCacheAsync(string title, string? releasePageUrl, string cacheKey, ArtworkPriority priority, bool forceRefresh, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_cacheDirectory);
		string text = (IsRutorReleasePage(releasePageUrl) ? "_v21" : "_v20");
		string metadataPath = Path.Combine(_cacheDirectory, cacheKey + text + ".json");
		if (!forceRefresh && priority != ArtworkPriority.ModalDetails)
		{
			GameArtwork cachedArtwork = await ReadCacheAsync(metadataPath, title, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (cachedArtwork != null)
			{
				return cachedArtwork;
			}
		}

		string missPath = metadataPath + ".miss";
		if (forceRefresh || priority == ArtworkPriority.ModalDetails)
		{
			try { if (File.Exists(missPath)) File.Delete(missPath); } catch { }
		}
		else if (File.Exists(missPath))
		{
			TimeSpan cooldown = priority == ArtworkPriority.Visible ? TimeSpan.FromSeconds(20.0) : TimeSpan.FromSeconds(60.0);
			if (File.GetLastWriteTimeUtc(missPath) > DateTime.UtcNow - cooldown)
			{
				return null;
			}
			try { File.Delete(missPath); } catch { }
		}

		await EnsureAppIdIndexLoadedAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		bool acquiredBgGate = false;
		bool acquiredReqGate = false;

		// ModalDetails gets immediate priority (fast track, no waiting behind background tasks)
		if (priority == ArtworkPriority.Background)
		{
			await _backgroundGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			acquiredBgGate = true;
			try
			{
				await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				acquiredReqGate = true;
			}
			catch
			{
				_backgroundGate.Release();
				acquiredBgGate = false;
				throw;
			}
		}
		else if (priority == ArtworkPriority.Visible)
		{
			await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			acquiredReqGate = true;
		}

		try
		{
			TimeSpan timeoutSpan = priority == ArtworkPriority.ModalDetails ? TimeSpan.FromSeconds(20.0) : TimeSpan.FromSeconds(12.0);
			timeout.CancelAfter(timeoutSpan);
			CancellationToken requestToken = timeout.Token;
			string[] searchQueries = GetSearchQueries(title);
			string[] broadQueries = GetBroadQueries(searchQueries);
			string[] steamQueries = searchQueries.Take(2).Concat(broadQueries.Take(1)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

			GameArtwork winner = null;
			// Fast path: the title was resolved before, so skip the store search and
			// fetch the capsule straight from the CDN.
			if (TryGetIndexedAppId(title, out IndexedAppId indexed))
			{
				winner = await LoadCandidateArtworkAsync(new StoreSearchMatch(indexed.AppId, indexed.Name, string.Empty, "game", 1.0), title, requestToken, cancellationToken, trustedAppId: true).ConfigureAwait(continueOnCapturedContext: false);
			}

			if (winner == null)
			{
				using CancellationTokenSource race = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
				Task<GameArtwork?> steamTask = FindSteamArtworkAsync(title, steamQueries, searchQueries, broadQueries, race.Token, cancellationToken);
				winner = await AwaitSteamOrFallbackAsync(steamTask, () => FindBingArtworkAsync(searchQueries, cacheKey, race.Token, priority), race.Token).ConfigureAwait(continueOnCapturedContext: false);
				race.Cancel();
			}

			if (winner == null)
			{
				// Slow sources only run when everything above missed, so their requests
				// never compete with the visible cards.
				using CancellationTokenSource extraSources = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
				extraSources.CancelAfter(TimeSpan.FromSeconds(4.0));
				Task<GameArtwork?> releasePageTask = !string.IsNullOrWhiteSpace(releasePageUrl)
					? FindReleasePageArtworkAsync(releasePageUrl, cacheKey, extraSources.Token)
					: Task.FromResult<GameArtwork?>(null);
				winner = await FirstSuccessfulAsync(releasePageTask, FindLutrisArtworkAsync(searchQueries, cacheKey, extraSources.Token), FindWikipediaArtworkAsync(searchQueries, cacheKey, extraSources.Token)).ConfigureAwait(continueOnCapturedContext: false);
			}

			if (winner != null)
			{
				return await SaveArtworkAsync(winner.CoverPath, winner.BannerPath, metadataPath, cancellationToken, winner.OfficialReleaseDate, winner.MatchedTitle).ConfigureAwait(continueOnCapturedContext: false);
			}

			if (!requestToken.IsCancellationRequested && priority != ArtworkPriority.ModalDetails)
			{
				try
				{
					await File.WriteAllTextAsync(missPath, DateTime.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch
				{
				}
			}
			return null;
		}
		finally
		{
			if (acquiredReqGate)
			{
				_requestGate.Release();
			}
			if (acquiredBgGate)
			{
				_backgroundGate.Release();
			}
		}
	}

	private async Task<GameArtwork?> FindSteamArtworkAsync(string title, string[] steamQueries, string[] searchQueries, string[] broadQueries, CancellationToken requestToken, CancellationToken cancellationToken)
	{
		StoreSearchMatch[] matches;
		using (CancellationTokenSource searchTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestToken))
		{
			searchTimeout.CancelAfter(TimeSpan.FromSeconds(5.0));
			try
			{
				// The precise query answers almost every title, so ask it alone first and
				// only widen when it misses: one request instead of three keeps a whole
				// page of visible cards inside Steam's rate limits.
				IReadOnlyList<StoreSearchItem> firstSet = await SearchStoreThrottledAsync(steamQueries[0], searchTimeout.Token).ConfigureAwait(continueOnCapturedContext: false);
				matches = RankSearchResults(new IReadOnlyList<StoreSearchItem>[1] { firstSet }, title, searchQueries, broadQueries);
				if (matches.Length == 0 && steamQueries.Length > 1)
				{
					IReadOnlyList<StoreSearchItem>[] restSets = await Task.WhenAll(steamQueries.Skip(1).Select((string query) => SearchStoreThrottledAsync(query, searchTimeout.Token))).ConfigureAwait(continueOnCapturedContext: false);
					matches = RankSearchResults(restSets, title, searchQueries, broadQueries);
				}
			}
			catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
			{
				matches = Array.Empty<StoreSearchMatch>();
			}
		}
		IEnumerable<StoreSearchMatch> candidates = matches.Take(3);
		int? knownSteamAppId = GetKnownSteamAppId(title);
		if (knownSteamAppId.HasValue)
		{
			candidates = new StoreSearchMatch[1]
			{
				new StoreSearchMatch(knownSteamAppId.Value, title, string.Empty, "game", 1.0)
			}.Concat(candidates.Where((StoreSearchMatch match) => match.Id != knownSteamAppId.Value));
		}
		foreach (StoreSearchMatch candidate in candidates)
		{
			if (requestToken.IsCancellationRequested)
			{
				break;
			}
			GameArtwork artwork = await LoadCandidateArtworkAsync(candidate, title, requestToken, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (artwork != null)
			{
				return artwork;
			}
		}
		return null;
	}

	private static async Task<GameArtwork?> AwaitSteamOrFallbackAsync(Task<GameArtwork?> steamTask, Func<Task<GameArtwork?>> fallbackFactory, CancellationToken cancellationToken)
	{
		// Steam resolves the vast majority of titles in a few hundred milliseconds.
		// While it is busy, skip the extra image search entirely: one request less per
		// card keeps the whole visible page fast. Only hedge when Steam is slow.
		using CancellationTokenSource grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task graceTask = Task.Delay(TimeSpan.FromSeconds(1.2), grace.Token);
		Task first = await Task.WhenAny(steamTask, graceTask).ConfigureAwait(continueOnCapturedContext: false);
		grace.Cancel();

		if (first == steamTask)
		{
			GameArtwork artwork = await AwaitArtworkSafeAsync(steamTask).ConfigureAwait(continueOnCapturedContext: false);
			return artwork ?? await AwaitArtworkSafeAsync(fallbackFactory()).ConfigureAwait(continueOnCapturedContext: false);
		}

		return await FirstSuccessfulAsync(steamTask, fallbackFactory()).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task<GameArtwork?> FindReleasePageArtworkAsync(string releasePageUrl, string cacheKey, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(releasePageUrl) || !Uri.TryCreate(releasePageUrl, UriKind.Absolute, out Uri uri))
		{
			return null;
		}
		if (!IsRutorHost(uri.Host))
		{
			return null;
		}
		try
		{
			using HttpResponseMessage response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}
			string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (string.IsNullOrWhiteSpace(html))
			{
				return null;
			}

			Match steamMatch = Regex.Match(html, @"(?:steamdb\.info|store\.steampowered\.com)/app/(\d+)", RegexOptions.IgnoreCase);
			if (steamMatch.Success && int.TryParse(steamMatch.Groups[1].Value, out int appId) && appId > 0)
			{
				try
				{
					GameArtwork steamDirect = await DownloadDirectArtworkAsync(appId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					if (steamDirect != null && (!string.IsNullOrEmpty(steamDirect.CoverPath) || !string.IsNullOrEmpty(steamDirect.BannerPath)))
					{
						return steamDirect;
					}
				}
				catch
				{
				}
			}

			HtmlDocument doc = new HtmlDocument();
			doc.LoadHtml(html);
			HtmlNodeCollection imgNodes = doc.DocumentNode.SelectNodes("//center//img | //div[@id='details']//img | //table//img");
			if (imgNodes == null || imgNodes.Count == 0)
			{
				return null;
			}

			string coverUrl = null;
			string bannerUrl = null;
			foreach (HtmlNode img in imgNodes)
			{
				string src = img.GetAttributeValue("src", string.Empty);
				if (string.IsNullOrWhiteSpace(src))
				{
					continue;
				}
				if (src.StartsWith("//"))
				{
					src = "https:" + src;
				}
				if (!Uri.TryCreate(src, UriKind.Absolute, out Uri imgUri) || (imgUri.Scheme != "http" && imgUri.Scheme != "https"))
				{
					continue;
				}

				string lower = src.ToLowerInvariant();
				if (lower.Contains("cdnbunny.org") || lower.Contains("counter.yadro.ru") ||
					lower.Contains("kinopoisk.ru") || lower.Contains("imdb.snick.ru") ||
					lower.Contains("sample.gif") || lower.Contains("magnet.gif") ||
					lower.Contains("logo.jpg") || lower.Contains("top.gif") ||
					lower.Contains("lupa.gif") || lower.Contains("arrow") ||
					lower.Contains("rating") || lower.Contains("down.png") ||
					lower.Contains("mails.png") || lower.Contains("t.me"))
				{
					continue;
				}

				if (coverUrl == null)
				{
					coverUrl = src;
				}
				else if (bannerUrl == null && !string.Equals(coverUrl, src, StringComparison.OrdinalIgnoreCase))
				{
					bannerUrl = src;
					break;
				}
			}

			if (coverUrl == null)
			{
				return null;
			}

			string coverPath = await DownloadImageAsync(coverUrl, $"release_{cacheKey}_cover", cancellationToken, allowExternalHttps: true).ConfigureAwait(continueOnCapturedContext: false);
			if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath) || new FileInfo(coverPath).Length < 3000)
			{
				return null;
			}

			string bannerPath = null;
			if (!string.IsNullOrEmpty(bannerUrl))
			{
				bannerPath = await DownloadImageAsync(bannerUrl, $"release_{cacheKey}_banner", cancellationToken, allowExternalHttps: true).ConfigureAwait(continueOnCapturedContext: false);
				if (bannerPath != null && (!File.Exists(bannerPath) || new FileInfo(bannerPath).Length < 3000))
				{
					bannerPath = null;
				}
			}

			return new GameArtwork
			{
				CoverPath = coverPath,
				BannerPath = (bannerPath ?? coverPath),
				MatchedTitle = ""
			};
		}
		catch
		{
			return null;
		}
	}

	private static async Task<GameArtwork?> FirstSuccessfulAsync(params Task<GameArtwork?>[] tasks)
	{
		List<Task<GameArtwork?>> pending = tasks.ToList();
		while (pending.Count > 0)
		{
			Task<GameArtwork?> completed = await Task.WhenAny(pending).ConfigureAwait(continueOnCapturedContext: false);
			pending.Remove(completed);
			GameArtwork artwork = await AwaitArtworkSafeAsync(completed).ConfigureAwait(continueOnCapturedContext: false);
			if (artwork != null)
			{
				return artwork;
			}
		}
		return null;
	}

	private static async Task<GameArtwork?> AwaitArtworkSafeAsync(Task<GameArtwork?> task)
	{
		try
		{
			return await task.ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
			return null;
		}
	}

	private static StoreSearchMatch[] RankSearchResults(IEnumerable<IReadOnlyList<StoreSearchItem>> resultSets, string requestedTitle, string[] searchQueries, string[] broadQueries)
	{
		return (from item in resultSets.SelectMany((IReadOnlyList<StoreSearchItem> items) => items)
			select new StoreSearchMatch(item.Id, item.Name, item.TinyImage, item.Type, ArtworkTitleMatcher.IsCompatible(requestedTitle, item.Name) ? Math.Max(searchQueries.Max((string query) => MatchScore(query, item.Name)), (broadQueries.Length == 0) ? 0.0 : (0.8 * broadQueries.Max((string query) => MatchScore(query, item.Name)))) : 0.0) into item
			where item.Id > 0 && string.Equals(item.Type, "game", StringComparison.OrdinalIgnoreCase) && item.Score >= 0.66
			group item by item.Id into @group
			select @group.OrderByDescending((StoreSearchMatch item) => item.Score).First() into item
			orderby item.Score descending, string.Equals(item.Type, "game", StringComparison.OrdinalIgnoreCase) descending
			select item).Take(5).ToArray();
	}

	private async Task<GameArtwork?> LoadCandidateArtworkAsync(StoreSearchMatch candidate, string requestedTitle, CancellationToken requestToken, CancellationToken callerToken, bool trustedAppId = false)
	{
		try
		{
			// Header and hero live at deterministic CDN URLs. Starting them before the
			// metadata request removes that round trip from the critical path.
			Task<GameArtwork?> directTask = DownloadDirectArtworkAsync(candidate.Id, requestToken);
			string detailsName = (trustedAppId ? candidate.Name : string.Empty);
			string officialReleaseDate = string.Empty;
			string bannerUrl = string.Empty;
			if (!trustedAppId)
			{
				try
				{
				string requestUri = $"https://store.steampowered.com/api/appdetails?appids={candidate.Id}&l=english&cc=US";
				using HttpResponseMessage detailsResponse = await _httpClient.GetAsync(requestUri, requestToken).ConfigureAwait(continueOnCapturedContext: false);
				if (detailsResponse.IsSuccessStatusCode)
				{
					using JsonDocument detailsJson = JsonDocument.Parse(await detailsResponse.Content.ReadAsStringAsync(requestToken).ConfigureAwait(continueOnCapturedContext: false));
					if (detailsJson.RootElement.TryGetProperty(candidate.Id.ToString(), out var value) && value.TryGetProperty("success", out var value2) && value2.GetBoolean() && value.TryGetProperty("data", out var value3))
					{
						if (!value3.TryGetProperty("type", out var typeElement) || !string.Equals(typeElement.GetString(), "game", StringComparison.OrdinalIgnoreCase))
						{
							return null;
						}
						// Defend against search results that point to an unrelated game: the
						// store page name must still describe the requested title.
						detailsName = ReadString(value3, "name");
						if (!string.IsNullOrWhiteSpace(detailsName) &&
							(!ArtworkTitleMatcher.IsCompatible(requestedTitle, detailsName) ||
							 !ArtworkTitleMatcher.IsCompatible(candidate.Name, detailsName)))
						{
							return null;
						}
						JsonElement releaseDate;
						officialReleaseDate = (value3.TryGetProperty("release_date", out releaseDate) ? ReadString(releaseDate, "date") : string.Empty);
						bannerUrl = ReadString(value3, "background_raw");
						if (string.IsNullOrWhiteSpace(bannerUrl))
						{
							bannerUrl = ReadString(value3, "background");
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				// Store metadata is optional: the CDN assets are still usable.
			}
			}
			GameArtwork direct = await directTask.ConfigureAwait(continueOnCapturedContext: false);
			string coverPath = direct?.CoverPath;
			string bannerPath = direct?.BannerPath;
			if (!IsValidWideBanner(bannerPath))
			{
				bannerPath = null;
			}
			if (bannerPath == null && !string.IsNullOrWhiteSpace(bannerUrl))
			{
				string text = await DownloadImageAsync(bannerUrl, $"{candidate.Id}_banner_v3", requestToken).ConfigureAwait(continueOnCapturedContext: false);
				if (IsValidWideBanner(text))
				{
					bannerPath = text;
				}
			}
			if (coverPath == null && !string.IsNullOrWhiteSpace(candidate.TinyImage))
			{
				coverPath = await DownloadImageAsync(candidate.TinyImage, $"{candidate.Id}_storesearch_cover_v1", requestToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			// A 231px capsule turns into mush on a card; leave the slot to a source that
			// can provide a proper cover instead.
			if (coverPath != null && TryGetImageDimensions(coverPath, out int coverWidth, out _) && coverWidth < 320)
			{
				coverPath = null;
			}
			if (coverPath == null && bannerPath == null)
			{
				return null;
			}
			string matchedTitle = (string.IsNullOrWhiteSpace(detailsName) ? candidate.Name : detailsName);
			RememberSteamAppId(requestedTitle, candidate.Id, matchedTitle);
			return new GameArtwork
			{
				CoverPath = (coverPath ?? bannerPath),
				BannerPath = (bannerPath ?? string.Empty),
				OfficialReleaseDate = officialReleaseDate,
				MatchedTitle = matchedTitle
			};
		}
		catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
		{
			return null;
		}
		catch when (!callerToken.IsCancellationRequested)
		{
			return await BuildFallbackArtworkAsync(candidate, callerToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task<IReadOnlyList<StoreSearchItem>> SearchStoreAsync(string query, CancellationToken cancellationToken)
	{
		string searchUrl = "https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(query) + "&l=english&cc=US";
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				using HttpResponseMessage response = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (!response.IsSuccessStatusCode)
				{
					if (attempt >= 2 || (response.StatusCode != HttpStatusCode.TooManyRequests && response.StatusCode < HttpStatusCode.InternalServerError))
					{
						return Array.Empty<StoreSearchItem>();
					}
					await Task.Delay(600 * (attempt + 1), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					continue;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
				if (!jsonDocument.RootElement.TryGetProperty("items", out var value) || value.ValueKind != JsonValueKind.Array)
				{
					return Array.Empty<StoreSearchItem>();
				}
				return (from item in value.EnumerateArray()
					select new StoreSearchItem(ReadInt(item, "id"), ReadString(item, "name"), ReadString(item, "tiny_image"), NormalizeStoreType(ReadString(item, "type")))).ToList();
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch when (attempt < 2)
			{
				await Task.Delay(600 * (attempt + 1), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
				return Array.Empty<StoreSearchItem>();
			}
		}
		return Array.Empty<StoreSearchItem>();
	}

	private static bool IsRutorReleasePage(string? releasePageUrl)
	{
		if (Uri.TryCreate(releasePageUrl, UriKind.Absolute, out Uri result))
		{
			return IsRutorHost(result.Host);
		}
		return false;
	}

	private static bool IsRutorHost(string host)
	{
		return host.Contains("rutor", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryMakeSafeImageUrl(Uri pageUri, string source, out string imageUrl)
	{
		imageUrl = string.Empty;
		if (string.IsNullOrWhiteSpace(source) || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(pageUri, HtmlEntity.DeEntitize(source), out Uri result) || (result.Scheme != Uri.UriSchemeHttps && result.Scheme != Uri.UriSchemeHttp) || result.Host.Length < 4 || result.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || result.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		imageUrl = result.AbsoluteUri;
		return true;
	}

	private async Task<GameArtwork?> FindBingArtworkAsync(string[] queries, string cacheKey, CancellationToken cancellationToken, ArtworkPriority priority = ArtworkPriority.Background)
	{
		// Bing starts throttling as soon as several image searches overlap, so keep
		// the fan-out small and pace the requests.
		await _bingGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			if (priority != ArtworkPriority.ModalDetails)
			{
				await Task.Delay(100, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			return await FindBingArtworkCoreAsync(queries, cacheKey, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			_bingGate.Release();
		}
	}

	private async Task<GameArtwork?> FindBingArtworkCoreAsync(string[] queries, string cacheKey, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(12.0));
		foreach (string item in queries.Take(2))
		{
			try
			{
				string requestUri = "https://www.bing.com/images/search?q=" + Uri.EscapeDataString(item + " video game cover artwork") + "&qft=+filterui:imagesize-large";
				using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
				if (!response.IsSuccessStatusCode)
				{
					continue;
				}
				HtmlDocument document = new HtmlDocument();
				document.LoadHtml(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(continueOnCapturedContext: false));
				HtmlNodeCollection htmlNodeCollection = document.DocumentNode.SelectNodes("//a[contains(@class,'iusc') and @m]");
				if (htmlNodeCollection == null || htmlNodeCollection.Count == 0)
				{
					// If large size filter produced no results, fallback without it
					string fallbackUri = "https://www.bing.com/images/search?q=" + Uri.EscapeDataString(item + " game cover");
					using HttpResponseMessage fallbackResp = await _httpClient.GetAsync(fallbackUri, timeout.Token).ConfigureAwait(false);
					if (fallbackResp.IsSuccessStatusCode)
					{
						document.LoadHtml(await fallbackResp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
						htmlNodeCollection = document.DocumentNode.SelectNodes("//a[contains(@class,'iusc') and @m]");
					}
				}
				if (htmlNodeCollection == null)
				{
					continue;
				}
				List<(string Url, double Score, string Title)> list = new List<(string, double, string)>();
				foreach (HtmlNode item2 in htmlNodeCollection)
				{
					string attributeValue = item2.GetAttributeValue("m", string.Empty);
					if (string.IsNullOrWhiteSpace(attributeValue))
					{
						continue;
					}
					try
					{
						using JsonDocument jsonDocument = JsonDocument.Parse(HtmlEntity.DeEntitize(attributeValue));
						JsonElement rootElement = jsonDocument.RootElement;
						JsonElement value;
						string text = (rootElement.TryGetProperty("murl", out value) ? value.GetString() : null);
						JsonElement value2;
						string imageTitle = (rootElement.TryGetProperty("t", out value2) ? value2.GetString() : string.Empty);
						if (TryMakeSafeImageUrl(new Uri("https://www.bing.com"), text ?? string.Empty, out string imageUrl) && !Regex.IsMatch(imageUrl + " " + imageTitle, "(logo|avatar|smiley|emoji|flag|icon|pixel|spacer|schematic|diagram|blueprint|manual|drawing|boiler|kotel|fireplace|stove|clipart|placeholder|screenshot|gameplay|walkthrough|trailer|gameplay|video|review|character|costume|plush|toy|lego|minecraft|roblox)", RegexOptions.IgnoreCase))
						{
							double num = queries.Max((string searchQuery) => MatchScore(searchQuery, imageTitle ?? string.Empty));
							if (num >= 0.45)
							{
								list.Add((imageUrl, num, imageTitle ?? string.Empty));
							}
						}
					}
					catch (JsonException)
					{
					}
				}
				foreach (var item3 in list.OrderByDescending<(string Url, double Score, string Title), double>(((string Url, double Score, string Title) item) => item.Score).Take(6))
				{
					string value3 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item3.Url))).ToLowerInvariant();
					string text2 = await DownloadImageAsync(item3.Url, $"bing_{cacheKey}_{value3}_cover_v3", timeout.Token, allowExternalHttps: true).ConfigureAwait(continueOnCapturedContext: false);
					if (text2 != null && File.Exists(text2) && new FileInfo(text2).Length >= 15000 && (!TryGetImageDimensions(text2, out var width, out var height) || width <= 0 || height <= 0 || (!((double)width / (double)height >= 2.5) && height >= 140)))
					{
						return new GameArtwork
						{
							CoverPath = text2,
							BannerPath = string.Empty,
							OfficialReleaseDate = string.Empty,
							MatchedTitle = (item3.Title ?? string.Empty)
						};
					}
				}
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				return null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
			}
		}
		return null;
	}

	private async Task<GameArtwork?> FindLutrisArtworkAsync(string[] queries, string cacheKey, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10.0));
		foreach (string query in queries.Take(3))
		{
			try
			{
				string requestUri = "https://lutris.net/games/?q=" + Uri.EscapeDataString(query);
				using (HttpResponseMessage response = await _httpClient.GetAsync(requestUri, timeout.Token).ConfigureAwait(continueOnCapturedContext: false))
				{
					if (!response.IsSuccessStatusCode)
					{
						continue;
					}
					HtmlDocument document = new HtmlDocument();
					HtmlDocument htmlDocument = document;
					htmlDocument.LoadHtml(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(continueOnCapturedContext: false));
					HtmlNodeCollection htmlNodeCollection = document.DocumentNode.SelectNodes("//a[starts-with(@href, '/games/')]");
					if (htmlNodeCollection == null)
					{
						continue;
					}
					var candidates = new List<(string Slug, double Score)>();
					foreach (HtmlNode link in htmlNodeCollection)
					{
							Match match = Regex.Match(link.GetAttributeValue("href", string.Empty), "^/games/([^/?#]+)/?$");
							if (!match.Success)
								continue;
							string slug = match.Groups[1].Value;
							string linkTitle = HtmlEntity.DeEntitize(link.InnerText).Trim();
							string candidate = string.IsNullOrWhiteSpace(linkTitle) ? slug.Replace('-', ' ') : linkTitle;
							candidates.Add((slug, ArtworkTitleMatcher.IsCompatible(queries[0], candidate) && ArtworkTitleMatcher.IsCompatible(queries[0], slug.Replace('-', ' ')) ? Math.Max(MatchScore(query, candidate), MatchScore(query, slug.Replace('-', ' '))) : 0.0));
					}

					foreach (var candidate in candidates.Where(candidate => candidate.Score >= 0.45)
						.OrderByDescending(candidate => candidate.Score).Take(5))
					{
						string url = "https://lutris.net/games/cover/" + candidate.Slug + ".jpg";
						string path = await DownloadImageAsync(url, "lutris_" + cacheKey + "_cover_v1", timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
						if (path != null)
						{
							return new GameArtwork
							{
								CoverPath = path,
								BannerPath = string.Empty,
								OfficialReleaseDate = string.Empty,
								MatchedTitle = candidate.Slug.Replace('-', ' ')
							};
						}
					}
				}
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				return null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
			}
		}
		return null;
	}

	private async Task<GameArtwork?> FindWikipediaArtworkAsync(string[] queries, string cacheKey, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10.0));
		foreach (string query in queries.Take(3))
		{
			try
			{
				string requestUri = "https://en.wikipedia.org/w/rest.php/v1/search/page?q=" + Uri.EscapeDataString(query) + "&limit=5";
				using (HttpResponseMessage response = await _httpClient.GetAsync(requestUri, timeout.Token).ConfigureAwait(continueOnCapturedContext: false))
				{
					if (!response.IsSuccessStatusCode)
					{
						continue;
					}
					using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(continueOnCapturedContext: false));
					if (!json.RootElement.TryGetProperty("pages", out var value) || value.ValueKind != JsonValueKind.Array)
					{
						goto end_IL_01f3;
					}
					JsonElement value2;
					var enumerable = (from page in value.EnumerateArray()
						select new
						{
							Title = ReadString(page, "title"),
							Thumbnail = (page.TryGetProperty("thumbnail", out value2) ? ReadString(value2, "url") : string.Empty)
						} into page
						where !string.IsNullOrWhiteSpace(page.Thumbnail)
						select new
						{
							Title = page.Title,
							Thumbnail = page.Thumbnail,
							Score = ArtworkTitleMatcher.IsCompatible(queries[0], page.Title) ? MatchScore(query, page.Title) : 0.0
						} into page
						where page.Score >= 0.55
						orderby page.Score descending
						select page).Take(3);
					foreach (var item in enumerable)
					{
						string text = await DownloadImageAsync(item.Thumbnail, "wiki_" + cacheKey + "_cover_v1", timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
						if (text != null)
						{
							return new GameArtwork
							{
								CoverPath = text,
								BannerPath = string.Empty,
								OfficialReleaseDate = string.Empty,
								MatchedTitle = (item.Title ?? string.Empty)
							};
						}
					}
					goto end_IL_0090;
					end_IL_01f3:;
				}
				end_IL_0090:;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				return null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
			}
		}
		return null;
	}

	private async Task<IReadOnlyList<StoreSearchItem>> SearchStoreThrottledAsync(string query, CancellationToken cancellationToken)
	{
		await _steamSearchGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			return await SearchStoreAsync(query, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			_steamSearchGate.Release();
		}
	}

	private static string NormalizeStoreType(string type)
	{
		// The storesearch API reports games as "app"; the rest of the pipeline filters
		// on "game", which used to drop every search result.
		return string.Equals(type, "app", StringComparison.OrdinalIgnoreCase) ? "game" : type;
	}

	private static string[] GetBroadQueries(string[] mainQueries)
	{
		return (from query in mainQueries
			select Regex.Match(query, "^(.{3,}?)\\s*(?::|\\s[-\\u2013\\u2014]\\s)") into match
			where match.Success
			select match.Groups[1].Value.Trim() into query
			where query.Length >= 3 && !mainQueries.Contains<string>(query, StringComparer.OrdinalIgnoreCase)
			select query).Distinct<string>(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
	}

	private static string[] GetSearchQueries(string title)
	{
		string input = title.Split('|')[0];
		input = Regex.Replace(input, "\\[[^\\]]*\\]|\\([^\\)]*\\)", " ");
		// Strip release noise that follows the name, but keep numbers that belong to
		// the title itself ("Metro 2033", "Cyberpunk 2077", "FIFA 23").
		input = Regex.Replace(input, "\\b(?:v\\s*\\d[\\w.]*|repack|repacked|fitgirl|dodi|rutor|torrent)\\b.*$", " ", RegexOptions.IgnoreCase);
		input = Regex.Replace(input, "\\s*(?:\\||\\bpc\\b|\\bportable\\b|\\bgog\\b|\\bscene\\b)\\s*$", " ", RegexOptions.IgnoreCase);
		input = Regex.Replace(input, "\\s+", " ").Trim(' ', '-', ':', '|');
		string text = Regex.Replace(input, "(?:\\s*[-:&+]|\\s+with|\\s+including)?\\s*(?:all\\s+)?(?:dlc'?s?|soundtrack|artbook|expansions?|season\\s+pass)(?:\\s+bundle|\\s+edition)?\\b.*$", "", RegexOptions.IgnoreCase).Trim(' ', '-', ':', '|', '&', '+');
		string text2 = Regex.Replace(input, "(?:\\s*[-:|]?\\s*)(?:director'?s?\\s+cut|complete(?:\\s+the\\s+set)?|ultimate|definitive|maximum|deluxe|gold|premium|praetor|centurion|reloaded|enhanced|remastered|digital|standard|anniversary|collector'?s?|edition|bundle)(?:\\s+edition)?\\s*$", "", RegexOptions.IgnoreCase).Trim(' ', '-', ':', '|');
		string input2 = Regex.Replace(input, "\\b(complete\\s+the\\s+set|director'?s?\\s+cut|ultimate|definitive|maximum|deluxe|gold|premium|praetor|centurion|reloaded|enhanced|remastered|digital|standard|anniversary|collector'?s?|edition|bundle|repack|portable)\\b", " ", RegexOptions.IgnoreCase);
		input2 = Regex.Replace(input2, "\\s+", " ").Trim(' ', '-', ':', '|');
		string text3 = Regex.Replace(input, "(?<=\\p{L})\\.(?=\\p{L})", "");
		// "Call of Duty: Ghosts - Ghosts Deluxe Edition" only resolves in the store as
		// "Call of Duty: Ghosts"; the first dash segment keeps the brand and subtitle.
		string text4 = Regex.Split(input, "\\s+-\\s+")[0].Trim(' ', '-', ':', '|');
		return new string[6] { input, text4, input2, text, text2, text3 }.Where((string query) => query.Length >= 2).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static int? GetKnownSteamAppId(string title)
	{
		if (Normalize(title).StartsWith("dying light the beast", StringComparison.Ordinal))
		{
			return 3008130;
		}
		return null;
	}

	private bool TryGetIndexedAppId(string title, out IndexedAppId indexed)
	{
		indexed = null;
		string key = Normalize(title);
		if (key.Length < 2 || !_appIdIndex.TryGetValue(key, out IndexedAppId entry))
		{
			return false;
		}
		// Only trust the stored entry while it still describes the requested game.
		if (!ArtworkTitleMatcher.IsCompatible(title, entry.Name))
		{
			return false;
		}
		indexed = entry;
		return true;
	}

	private async Task EnsureAppIdIndexLoadedAsync(CancellationToken cancellationToken)
	{
		if (_appIdIndexLoaded)
		{
			return;
		}
		await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			if (_appIdIndexLoaded)
			{
				return;
			}
			try
			{
				if (File.Exists(AppIdIndexPath))
				{
					Dictionary<string, IndexedAppId> entries = JsonSerializer.Deserialize<Dictionary<string, IndexedAppId>>(await File.ReadAllTextAsync(AppIdIndexPath, cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
					if (entries != null)
					{
						foreach (KeyValuePair<string, IndexedAppId> entry in entries)
						{
							if (entry.Value != null && entry.Value.AppId > 0)
							{
								_appIdIndex[entry.Key] = entry.Value;
							}
						}
					}
				}
			}
			catch
			{
				// A damaged index only costs one extra store search per title.
			}
			_appIdIndexLoaded = true;
		}
		finally
		{
			_indexGate.Release();
		}
	}

	private void RememberSteamAppId(string title, int appId, string storeName)
	{
		string key = Normalize(title);
		if (key.Length < 2 || appId <= 0)
		{
			return;
		}
		string name = (string.IsNullOrWhiteSpace(storeName) ? title : storeName);
		if (_appIdIndex.TryGetValue(key, out IndexedAppId existing) && existing.AppId == appId && string.Equals(existing.Name, name, StringComparison.Ordinal))
		{
			return;
		}
		_appIdIndex[key] = new IndexedAppId(appId, name);
		_ = PersistAppIdIndexAsync();
	}

	private async Task PersistAppIdIndexAsync()
	{
		try
		{
			await _indexGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				Directory.CreateDirectory(_cacheDirectory);
				Dictionary<string, IndexedAppId> snapshot = new Dictionary<string, IndexedAppId>(_appIdIndex, StringComparer.OrdinalIgnoreCase);
				await File.WriteAllTextAsync(AppIdIndexPath, JsonSerializer.Serialize(snapshot)).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				_indexGate.Release();
			}
		}
		catch
		{
		}
	}

	private async Task<GameArtwork?> BuildFallbackArtworkAsync(StoreSearchMatch candidate, CancellationToken cancellationToken)
	{
		GameArtwork direct = await DownloadDirectArtworkAsync(candidate.Id, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		string text = direct?.CoverPath;
		if (text == null && !string.IsNullOrWhiteSpace(candidate.TinyImage))
		{
			text = await DownloadImageAsync(candidate.TinyImage, $"{candidate.Id}_storesearch_cover_v1", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		if (text == null && direct?.BannerPath == null)
		{
			return null;
		}
		return new GameArtwork
		{
			CoverPath = (text ?? direct?.BannerPath ?? string.Empty),
			BannerPath = (direct?.BannerPath ?? string.Empty),
			MatchedTitle = candidate.Name
		};
	}

	private async Task<GameArtwork?> DownloadDirectArtworkAsync(int appId, CancellationToken cancellationToken)
	{
		Task<string?> coverTask = DownloadFirstAvailableAsync(appId, new string[3] { "header.jpg", "capsule_616x353.jpg", "capsule_231x87.jpg" }, $"{appId}_cover_v3", cancellationToken);
		Task<string?> bannerTask = DownloadFirstAvailableAsync(appId, "library_hero.jpg", $"{appId}_library_hero_v1", cancellationToken);
		await Task.WhenAll(coverTask, bannerTask).ConfigureAwait(continueOnCapturedContext: false);
		string cover = await coverTask.ConfigureAwait(continueOnCapturedContext: false);
		string text = await bannerTask.ConfigureAwait(continueOnCapturedContext: false);
		if (!IsValidWideBanner(text))
		{
			text = null;
		}
		return (cover == null && text == null) ? null : new GameArtwork
		{
			CoverPath = (cover ?? text),
			BannerPath = (text ?? string.Empty)
		};
	}

	private static bool TryGetImageDimensions(string? path, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return false;
		}
		try
		{
			using FileStream fileStream = File.OpenRead(path);
			Span<byte> buffer = stackalloc byte[30];
			int num = fileStream.Read(buffer);
			if (num >= 24 && buffer[0] == 137 && buffer[1] == 80 && buffer[2] == 78 && buffer[3] == 71)
			{
				width = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(16, 4));
				height = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(20, 4));
				return width > 0 && height > 0;
			}
			if (num >= 30 && buffer.Slice(0, 4).SequenceEqual("RIFF"u8) && buffer.Slice(8, 4).SequenceEqual("WEBP"u8) && buffer.Slice(12, 4).SequenceEqual("VP8X"u8))
			{
				width = 1 + buffer[24] + (buffer[25] << 8) + (buffer[26] << 16);
				height = 1 + buffer[27] + (buffer[28] << 8) + (buffer[29] << 16);
				return width > 0 && height > 0;
			}
			fileStream.Position = 0L;
			using BinaryReader binaryReader = new BinaryReader(fileStream);
			if (binaryReader.ReadByte() != byte.MaxValue || binaryReader.ReadByte() != 216)
			{
				return false;
			}
			while (fileStream.Position + 4 <= fileStream.Length)
			{
				if (binaryReader.ReadByte() != byte.MaxValue)
				{
					continue;
				}
				while (true)
				{
					byte b = binaryReader.ReadByte();
					switch (b)
					{
					case byte.MaxValue:
						continue;
					case 217:
					case 218:
						return false;
					default:
					{
						int num2 = (binaryReader.ReadByte() << 8) | binaryReader.ReadByte();
						if (num2 < 2 || fileStream.Position + num2 - 2 > fileStream.Length)
						{
							return false;
						}
						bool flag;
						switch (b)
						{
						case 192:
						case 193:
						case 194:
						case 195:
						case 197:
						case 198:
						case 199:
						case 201:
						case 202:
						case 203:
						case 205:
						case 206:
						case 207:
							flag = true;
							break;
						default:
							flag = false;
							break;
						}
						if (flag)
						{
							binaryReader.ReadByte();
							height = (binaryReader.ReadByte() << 8) | binaryReader.ReadByte();
							width = (binaryReader.ReadByte() << 8) | binaryReader.ReadByte();
							return width > 0 && height > 0;
						}
						fileStream.Seek(num2 - 2, SeekOrigin.Current);
						break;
					}
					case 0:
					case 1:
					case 208:
					case 209:
					case 210:
					case 211:
					case 212:
					case 213:
					case 214:
					case 215:
					case 216:
						break;
					}
					break;
				}
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		catch (ArgumentException)
		{
		}
		return false;
	}

	private static bool IsValidWideBanner(string? path)
	{
		if (TryGetImageDimensions(path, out var width, out var height))
		{
			return IsWideBannerSize(width, height);
		}
		return false;
	}

	private static bool IsWideBannerSize(int width, int height)
	{
		if (width >= 1000 && height >= 300)
		{
			return (double)width / (double)height >= 2.7;
		}
		return false;
	}

	private async Task<string?> DownloadFirstAvailableAsync(int appId, string fileName, string cacheName, CancellationToken cancellationToken)
		=> await DownloadFirstAvailableAsync(appId, new string[1] { fileName }, cacheName, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

	private async Task<string?> DownloadFirstAvailableAsync(int appId, string[] fileNames, string cacheName, CancellationToken cancellationToken)
	{
		foreach (string fileName in fileNames)
		{
			string[] array = new string[2]
			{
				$"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/{fileName}",
				$"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/{fileName}"
			};
			string[] array2 = array;
			foreach (string url in array2)
			{
				string text = await DownloadImageAsync(url, cacheName, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (text != null)
				{
					return text;
				}
			}
		}
		return null;
	}

	private async Task<GameArtwork?> SaveArtworkAsync(string? coverPath, string? bannerPath, string metadataPath, CancellationToken cancellationToken, string officialReleaseDate = "", string matchedTitle = "")
	{
		if (coverPath == null && bannerPath == null)
		{
			return null;
		}
		GameArtwork result = new GameArtwork
		{
			CoverPath = (coverPath ?? bannerPath),
			BannerPath = (bannerPath ?? string.Empty),
			OfficialReleaseDate = officialReleaseDate,
			MatchedTitle = matchedTitle ?? string.Empty
		};
		await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(result), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			File.Delete(metadataPath + ".miss");
		}
		catch
		{
		}
		return result;
	}

	private async Task<GameArtwork?> ReadCacheAsync(string metadataPath, string requestedTitle, CancellationToken cancellationToken)
	{
		if (!File.Exists(metadataPath))
		{
			return null;
		}
		try
		{
			GameArtwork gameArtwork = JsonSerializer.Deserialize<GameArtwork>(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
			// Entries written by older versions may point to a different game. Only use
			// cached artwork when its stored title is still compatible with the request.
			if (gameArtwork != null &&
				(string.IsNullOrWhiteSpace(gameArtwork.MatchedTitle) || ArtworkTitleMatcher.IsCompatible(requestedTitle, gameArtwork.MatchedTitle)) &&
				File.Exists(gameArtwork.CoverPath) && (string.IsNullOrWhiteSpace(gameArtwork.BannerPath) || File.Exists(gameArtwork.BannerPath)))
			{
				return gameArtwork;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
		}
		return null;
	}

	private async Task<string?> DownloadImageAsync(string url, string name, CancellationToken cancellationToken, bool allowExternalHttps = false)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri result) || (result.Scheme != Uri.UriSchemeHttps && result.Scheme != Uri.UriSchemeHttp) || (!result.Host.Equals("steamstatic.com", StringComparison.OrdinalIgnoreCase) && !result.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) && !result.Host.Equals("upload.wikimedia.org", StringComparison.OrdinalIgnoreCase) && !result.Host.Equals("lutris.net", StringComparison.OrdinalIgnoreCase) && !result.Host.EndsWith(".lutris.net", StringComparison.OrdinalIgnoreCase) && (!allowExternalHttps || Uri.CheckHostName(result.Host) != UriHostNameType.Dns || result.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || result.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))))
		{
			return null;
		}

		string basePath = Path.Combine(_cacheDirectory, name);
		string webpPath = basePath + ".webp";
		if (IsUsableFile(webpPath))
		{
			return webpPath;
		}

		// Images cached by older versions were stored as full-size JPEGs. Convert
		// them lazily the first time they are requested again, and keep the JPEG
		// only if WebP would not be smaller.
		string legacyPath = basePath + ".jpg";
		if (IsUsableFile(legacyPath))
		{
			// Keep the JPEG until the next cache maintenance run: cards loaded before
			// this request may still reference it for the current session.
			return await ArtworkImageOptimizer.OptimizeCachedFileAsync(
				legacyPath,
				ArtworkImageOptimizer.DetectKind(name),
				cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}

		using HttpResponseMessage response = await _httpClient.GetAsync(result, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (response.IsSuccessStatusCode)
		{
			long? contentLength = response.Content.Headers.ContentLength;
			if (!contentLength.HasValue || contentLength.GetValueOrDefault() <= 20000000)
			{
				string text = response.Content.Headers.ContentType?.MediaType;
				if (text != null && !text.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}
				string temporary = basePath + ".download";
				try
				{
					await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
					{
						await using FileStream destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
						await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					if (!IsUsableFile(temporary) || new FileInfo(temporary).Length > 20000000)
					{
						return null;
					}

					// Store a downscaled WebP instead of the original CDN asset. This is
					// the main storage win: Steam heroes are up to 3840px wide and a few MB.
					return await ArtworkImageOptimizer.ConvertToWebpAsync(
						temporary,
						basePath,
						ArtworkImageOptimizer.DetectKind(name),
						cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					TryDeleteFile(temporary);
				}
			}
		}
		return null;
	}

	private static bool IsUsableFile(string path)
	{
		try
		{
			return File.Exists(path) && new FileInfo(path).Length > 0;
		}
		catch
		{
			return false;
		}
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
		}
	}

	private static int ReadInt(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var value2))
		{
			return 0;
		}
		return value2;
	}

	private static string ReadString(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return string.Empty;
		}
		return value.GetString() ?? string.Empty;
	}

	private static double MatchScore(string query, string candidate)
	{
		HashSet<string> hashSet = TokenizeForMatch(query);
		HashSet<string> hashSet2 = TokenizeForMatch(candidate);
		if (hashSet.Count == 0 || hashSet2.Count == 0)
		{
			return 0.0;
		}
		HashSet<string> hashSet3 = ExtractNumericTokens(hashSet);
		HashSet<string> hashSet4 = ExtractNumericTokens(hashSet2);
		if (hashSet3.Count > 0 && hashSet4.Count > 0)
		{
			if (!hashSet3.Overlaps(hashSet4))
			{
				return 0.0;
			}
		}
		else
		{
			if (hashSet3.Count > 0 && hashSet4.Count == 0)
			{
				return 0.0;
			}
			if (hashSet3.Count == 0 && hashSet4.Count > 0)
			{
				return 0.0;
			}
		}
		if (hashSet.SetEquals(hashSet2))
		{
			return 1.0;
		}
		string primaryToken = GetPrimaryToken(query);
		if (!string.IsNullOrEmpty(primaryToken) && !hashSet2.Contains(primaryToken))
		{
			return 0.0;
		}
		string text = string.Join(' ', hashSet.OrderBy<string, string>((string word) => word, StringComparer.Ordinal));
		string text2 = string.Join(' ', hashSet2.OrderBy<string, string>((string word) => word, StringComparer.Ordinal));
		if (text2.Contains(text, StringComparison.Ordinal))
		{
			return 0.9;
		}
		if (text.Contains(text2, StringComparison.Ordinal) && hashSet2.Count >= 2)
		{
			return 0.85;
		}
		return (double)hashSet.Intersect<string>(hashSet2, StringComparer.Ordinal).Count() / (double)Math.Max(hashSet.Count, hashSet2.Count);
	}

	private static HashSet<string> ExtractNumericTokens(HashSet<string> words)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (string word in words)
		{
			if (int.TryParse(word, out var result) && result >= 1 && result <= 99)
			{
				hashSet.Add(word);
			}
		}
		return hashSet;
	}

	private static string GetPrimaryToken(string title)
	{
		HashSet<string> ignored = GetIgnoredTokens();
		return (from match in Regex.Matches(Normalize(title), "[\\p{L}\\p{N}]+")
			select match.Value into token
			where token.Length >= 3 && !ignored.Contains(token)
			select token).FirstOrDefault() ?? string.Empty;
	}

	private static string Normalize(string value)
	{
		return string.Join(' ', from match in Regex.Matches(value.ToLowerInvariant(), "[\\p{L}\\p{N}]+")
			select match.Value);
	}

	private static HashSet<string> GetIgnoredTokens()
	{
		return new HashSet<string>(StringComparer.Ordinal)
		{
			"the", "a", "an", "and", "of", "in", "on", "for", "to", "with",
			"by", "at", "from", "game", "year", "goty", "edition", "bundle", "complete", "set",
			"ultimate", "definitive", "praetor", "centurion", "maximum", "deluxe", "gold", "premium", "reloaded", "enhanced",
			"remastered", "digital", "standard", "anniversary", "collector", "collectors", "repack", "portable", "rip", "directors",
			"director", "cut", "patch", "update", "v", "build", "multi", "rus", "eng", "steamrip",
			"dodi", "fitgirl", "gog", "pc", "all", "dlc", "dlcs", "soundtrack", "artbook", "pass",
			"season", "expansions", "expansion", "sid", "meier", "meiers", "tom", "clancy", "clancys", "marvel",
			"marvels"
		};
	}

	private static HashSet<string> TokenizeForMatch(string title)
	{
		HashSet<string> ignored = GetIgnoredTokens();
		return (from token in (from match in Regex.Matches(Normalize(title), "[\\p{L}\\p{N}]+")
				select match.Value).Select(NormalizeRomanNumeral)
			where !ignored.Contains(token)
			select token).ToHashSet<string>(StringComparer.Ordinal);
	}

	private static string NormalizeRomanNumeral(string token)
	{
		return token switch
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
			_ => token, 
		};
	}
}
