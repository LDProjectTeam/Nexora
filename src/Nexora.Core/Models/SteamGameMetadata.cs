using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexora.Core.Models
{
    /// <summary>
    /// Official metadata read from the Steam Store (appdetails endpoint).
    /// Kept separate from release metadata: developer, publisher, genres and the
    /// official description never replace the repack author, source or build type.
    /// </summary>
    public sealed class SteamGameMetadata
    {
        public int AppId { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Store item type reported by Steam ("game" for games).</summary>
        public string Type { get; set; } = string.Empty;

        public string ShortDescription { get; set; } = string.Empty;

        /// <summary>Detailed description with Steam HTML markup intact.</summary>
        public string DetailedDescription { get; set; } = string.Empty;

        public List<string> Developers { get; set; } = new();

        public List<string> Publishers { get; set; } = new();

        public List<string> Genres { get; set; } = new();

        /// <summary>Platform keys: windows, mac, linux.</summary>
        public List<string> Platforms { get; set; } = new();

        public string ReleaseDateText { get; set; } = string.Empty;

        /// <summary>Year extracted from the official Steam release date, when it can be parsed.</summary>
        public int? ReleaseYear { get; set; }

        public bool ComingSoon { get; set; }

        public string HeaderImageUrl { get; set; } = string.Empty;

        public string BackgroundImageUrl { get; set; } = string.Empty;

        public string StoreUrl { get; set; } = string.Empty;

        /// <summary>Human readable list of supported languages, HTML stripped.</summary>
        public string SupportedLanguages { get; set; } = string.Empty;

        /// <summary>Minimum system requirements, HTML stripped. Empty when Steam has none.</summary>
        public string MinimumRequirements { get; set; } = string.Empty;

        /// <summary>Recommended system requirements, HTML stripped. Empty when Steam has none.</summary>
        public string RecommendedRequirements { get; set; } = string.Empty;

        public string MetacriticScore { get; set; } = string.Empty;

        public string Website { get; set; } = string.Empty;

        public DateTime FetchedAtUtc { get; set; }

        public string LanguageCode { get; set; } = "english";

        public bool IsEnglishDescriptionFallback { get; set; }

        [JsonIgnore]
        public bool HasDescription =>
            !string.IsNullOrWhiteSpace(ShortDescription) || !string.IsNullOrWhiteSpace(DetailedDescription);
    }

    /// <summary>Input used to resolve a game to a Steam Store page.</summary>
    public sealed class SteamMetadataRequest
    {
        public string Title { get; set; } = string.Empty;

        public string? AlternateTitle { get; set; }

        /// <summary>Raw release title. Used only to extract the release year and platforms.</summary>
        public string? RawTitle { get; set; }

        /// <summary>Explicit Steam AppID from project data, when available.</summary>
        public int? AppId { get; set; }

        /// <summary>Original release year. Prevents matching a remake / re-release by title alone.</summary>
        public int? ReleaseYear { get; set; }

        /// <summary>
        /// Steam Store language code: english, russian, ukrainian.
        /// The service falls back to english when a translation is missing.
        /// </summary>
        public string LanguageCode { get; set; } = "english";
    }

    public sealed class SteamMetadataLookupResult
    {
        public SteamGameMetadata? Metadata { get; set; }

        /// <summary>True when the returned metadata came from the local cache.</summary>
        public bool IsFromCache { get; set; }

        /// <summary>True when Steam results existed but could not be matched unambiguously.</summary>
        public bool IsAmbiguous { get; set; }

        /// <summary>True when the title was not found in the Steam Store at all.</summary>
        public bool IsNotFound { get; set; }

        /// <summary>Network / parsing error, if any. Cached metadata may still be available.</summary>
        public string? Error { get; set; }

        [JsonIgnore]
        public bool IsAvailable => Metadata != null;
    }
}
