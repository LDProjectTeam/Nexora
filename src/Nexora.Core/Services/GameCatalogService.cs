using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Loads game definitions from the built-in games.json plus any additional
    /// catalog URLs configured in AppSettings.
    /// </summary>
    public class GameCatalogService : IGameCatalog
    {
        private readonly ILogger<GameCatalogService> _logger;
        private readonly ISettingsService _settings;
        private readonly HttpClient _http;

        private List<GameDefinition> _games = new();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public GameCatalogService(ILogger<GameCatalogService> logger, ISettingsService settings, HttpClient http)
        {
            _logger = logger;
            _settings = settings;
            _http = http;
        }

        public async Task<IReadOnlyList<GameDefinition>> GetAllGamesAsync(CancellationToken ct = default)
        {
            if (_games.Count == 0)
                await RefreshAsync(ct);
            return _games.AsReadOnly();
        }

        public async Task<GameDefinition?> GetGameAsync(string gameId, CancellationToken ct = default)
        {
            var all = await GetAllGamesAsync(ct);
            return all.FirstOrDefault(g => g.Id == gameId);
        }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            var games = new List<GameDefinition>();

            // Load built-in catalog
            var builtIn = await LoadFromFileAsync(PathHelper.BuiltInCatalogPath);
            games.AddRange(builtIn);

            // Load additional catalogs from URLs
            foreach (var url in _settings.Settings.AdditionalCatalogUrls)
            {
                try
                {
                    var remote = await LoadFromUrlAsync(url, ct);
                    // Merge: remote entries override built-in by ID
                    foreach (var game in remote)
                    {
                        var existing = games.FindIndex(g => g.Id == game.Id);
                        if (existing >= 0)
                            games[existing] = game;
                        else
                            games.Add(game);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load remote catalog from {Url}", url);
                }
            }

            _games = games;
            _logger.LogInformation("Catalog loaded: {Count} game(s)", _games.Count);
        }

        private async Task<List<GameDefinition>> LoadFromFileAsync(string path)
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("Catalog file not found: {Path}", path);
                return new List<GameDefinition>();
            }

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var catalog = JsonSerializer.Deserialize<GameCatalogFile>(json, _jsonOptions);
                return catalog?.Games ?? new List<GameDefinition>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse catalog file: {Path}", path);
                return new List<GameDefinition>();
            }
        }

        private async Task<List<GameDefinition>> LoadFromUrlAsync(string url, CancellationToken ct)
        {
            if (File.Exists(url) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(url).LocalPath
                    : url;
                return await LoadFromFileAsync(filePath);
            }

            var json = await _http.GetStringAsync(url, ct);
            var catalog = JsonSerializer.Deserialize<GameCatalogFile>(json, _jsonOptions);
            return catalog?.Games ?? new List<GameDefinition>();
        }

        private class GameCatalogFile
        {
            [JsonPropertyName("catalogVersion")]
            public string CatalogVersion { get; set; } = "1.0";

            [JsonPropertyName("games")]
            public List<GameDefinition> Games { get; set; } = new();
        }
    }
}
