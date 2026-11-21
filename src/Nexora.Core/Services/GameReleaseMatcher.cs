using System;
using System.Text.RegularExpressions;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    /// <summary>Matches a release to one specific PC game, including its sequel number.</summary>
    public static class GameReleaseMatcher
    {
        private static readonly Regex ConsolePlatform = new(
            @"\b(?:PS[2-5]|PSP|PSVITA|PLAYSTATION(?:\s*[2-5])?|XBOX(?:\s*(?:360|ONE|SERIES\s*[XS]))?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RomanNumeral = new(
            @"\b(II|III|IV|V|VI|VII|VIII|IX|X)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReleaseNoise = new(
            @"\b(?:PC|WINDOWS|REPACK|REPACKED|PORTABLE|GOG|STEAMRIP|RIP|INSTALLER|SETUP|MULTI\s*\d+|RUS|ENG|RUSSIAN|ENGLISH|PROPHET|RELOADED|SKIDROW|FITGIRL|XATAB|DODI)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NonTitleChars = new(
            @"[^\p{L}\p{N}]", RegexOptions.Compiled);

        public static bool IsConsoleRelease(GameReleaseCandidate candidate)
        {
            if (candidate == null) return false;

            return ConsolePlatform.IsMatch(candidate.RawTitle ?? string.Empty)
                || ConsolePlatform.IsMatch(candidate.CleanTitle ?? string.Empty)
                || ConsolePlatform.IsMatch(candidate.AltTitle ?? string.Empty);
        }

        public static bool IsForGame(GameReleaseCandidate candidate, string gameTitle)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(gameTitle) || IsConsoleRelease(candidate))
                return false;

            return TitlesMatch(candidate.CleanTitle, gameTitle)
                || TitlesMatch(candidate.AltTitle, gameTitle);
        }

        public static bool TitlesMatch(string candidateTitle, string gameTitle)
        {
            var candidateKey = GetCanonicalKey(candidateTitle);
            var gameKey = GetCanonicalKey(gameTitle);
            return candidateKey.Length > 0 && candidateKey == gameKey;
        }

        private static string GetCanonicalKey(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;

            // Trackers often append release metadata after a bracket; the parser's clean title
            // normally removes it, but stripping here also protects callers using raw card names.
            var normalized = Regex.Replace(title, @"\([^)]*\)|\[[^]]*\]", " ");
            normalized = ReleaseNoise.Replace(normalized, " ");
            normalized = RomanNumeral.Replace(normalized, match => RomanToArabic(match.Value));
            var key = NonTitleChars.Replace(normalized, string.Empty).ToLowerInvariant();

            // Call of Duty: Black Ops (the first game) is commonly listed without “1”.
            if (key.EndsWith("callofdutyblackops", StringComparison.Ordinal))
                key += "1";
            else if (key == "blackops")
                key = "blackops1";

            return key;
        }

        private static string RomanToArabic(string value) => value.ToUpperInvariant() switch
        {
            "II" => "2", "III" => "3", "IV" => "4", "V" => "5", "VI" => "6",
            "VII" => "7", "VIII" => "8", "IX" => "9", "X" => "10", _ => value
        };
    }
}
