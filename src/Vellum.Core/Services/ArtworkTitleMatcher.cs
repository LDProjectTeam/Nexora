using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vellum.Core.Services;

/// <summary>
/// Rejects artwork candidates that cannot represent the requested game: a different
/// Call of Duty sub-series, a different sequel number or release year, or extra words
/// that point to another entry in the same series (for example "The Forest" vs
/// "Sons of the Forest").
/// </summary>
public static class ArtworkTitleMatcher
{
    private static readonly string[] CallOfDutyBranches =
    [
        "black ops cold war", "modern warfare", "black ops", "world at war",
        "advanced warfare", "infinite warfare", "call of duty ghosts", "vanguard",
        "wwii", "united offensive"
    ];

    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.Ordinal)
    {
        // Articles and connectors
        "the", "a", "an", "and", "of", "in", "on", "for", "to", "with", "by", "at", "from",
        // Platforms, stores and release formats
        "game", "video", "pc", "windows", "steam", "xbox", "xbox360", "xboxone",
        "playstation", "ps", "ps1", "ps2", "ps3", "ps4", "ps5", "nintendo", "switch", "wii",
        "mac", "linux", "android", "ios", "gog", "epic", "origin", "uplay",
        // Editions and extras
        "edition", "bundle", "complete", "set", "ultimate", "definitive", "deluxe", "gold",
        "premium", "goty", "remastered", "remaster", "enhanced", "digital", "standard",
        "anniversary", "collector", "collectors", "collection", "directors", "director", "cut",
        "season", "pass", "dlc", "dlcs", "soundtrack", "artbook", "expansion", "expansions",
        "update", "patch", "repack", "portable", "rip", "build", "multi", "rus", "eng",
        "full", "free", "download", "install", "offline", "online", "standalone",
        "crack", "cracked", "repacked", "extended", "special", "premium", "trial", "demo",
        // Box art descriptors
        "cover", "art", "artwork", "box", "boxart", "key", "poster", "banner", "image",
        "wallpaper", "hd", "uhd", "4k", "1080p", "2160p", "screenshot", "screenshots",
        "review", "trailer", "walkthrough", "guide", "cheats", "mod", "mods", "iso"
    };

    public static bool IsCompatible(string requestedTitle, string candidateTitle)
    {
        var requested = Normalize(requestedTitle);
        var candidate = Normalize(candidateTitle);
        if (requested.Length == 0 || candidate.Length == 0)
            return true;

        // Call of Duty titles share a prefix but differ by sub-series. A different
        // branch (Ghosts / Modern Warfare / Black Ops) is never the same game.
        if (requested.StartsWith("call of duty", StringComparison.Ordinal) &&
            candidate.StartsWith("call of duty", StringComparison.Ordinal) &&
            !string.Equals(GetCallOfDutyBranch(requested), GetCallOfDutyBranch(candidate), StringComparison.Ordinal))
        {
            return false;
        }

        var requestedNumbers = GetInstallments(requested);
        var candidateNumbers = GetInstallments(candidate);
        if (requestedNumbers.Count > 0 && candidateNumbers.Count > 0)
        {
            if (!requestedNumbers.Overlaps(candidateNumbers))
                return false;
        }
        else if (requestedNumbers.Count != candidateNumbers.Count)
        {
            // "Metro 2033" is not "Metro"; "Cold Fear 2" is not "Cold Fear".
            return false;
        }

        var requestedYears = GetYears(requested);
        var candidateYears = GetYears(candidate);
        if (requestedYears.Count > 0 && candidateYears.Count > 0 && !requestedYears.Overlaps(candidateYears))
            return false;

        var requestedTokens = Tokenize(requestedTitle);
        var candidateTokens = Tokenize(candidateTitle);
        if (requestedTokens.Count == 0 || candidateTokens.Count == 0)
            return true;

        var primaryToken = GetPrimaryToken(requestedTitle);
        if (primaryToken.Length > 0 &&
            !candidateTokens.Contains(primaryToken) &&
            !candidateTokens.Any(token => token.StartsWith(primaryToken, StringComparison.Ordinal) ||
                                          primaryToken.StartsWith(token, StringComparison.Ordinal)))
        {
            return false;
        }

        if (requestedTokens.Count == 1)
            return candidateTokens.Count == 1 && candidateTokens.Contains(primaryToken);

        if (requestedTokens.IsSubsetOf(candidateTokens))
        {
            // The candidate adds meaningful words, so it probably belongs to another
            // entry in the series ("The Forest" vs "The Forest Quartet").
            return candidateTokens.Count <= requestedTokens.Count;
        }

        // The candidate is a shorter store name ("The Long Dark" for
        // "The Long Dark: Quiet Apocalypse Bundle") or overlaps enough.
        var overlap = requestedTokens.Intersect(candidateTokens).Count();
        return overlap >= Math.Min(2, requestedTokens.Count) &&
               (double)overlap / requestedTokens.Count >= 0.5;
    }

    private static string GetCallOfDutyBranch(string title)
    {
        foreach (var branch in CallOfDutyBranches)
            if (title.Contains(branch, StringComparison.Ordinal))
                return branch;
        return string.Empty;
    }

    private static HashSet<string> GetInstallments(string normalizedTitle)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(normalizedTitle, @"\b(?:[1-9]|[1-9][0-9])\b"))
            result.Add(match.Value);
        return result;
    }

    private static HashSet<string> GetYears(string normalizedTitle)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(normalizedTitle, @"\b(?:19|20)\d{2}\b"))
            result.Add(match.Value);
        return result;
    }

    private static HashSet<string> Tokenize(string title)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(NormalizeRomanNumerals(title), @"[\p{L}\p{N}]+"))
        {
            var token = match.Value;
            if (token.Length < 2 && !char.IsDigit(token[0]))
                continue;
            if (IgnoredTokens.Contains(token))
                continue;
            if (Regex.IsMatch(token, @"^(?:19|20)\d{2}$"))
                continue;
            if (Regex.IsMatch(token, @"^\d{3,4}x\d{3,4}$"))
                continue;
            result.Add(token);
        }
        return result;
    }

    private static string GetPrimaryToken(string title)
    {
        foreach (Match match in Regex.Matches(NormalizeRomanNumerals(title), @"[\p{L}\p{N}]+"))
        {
            var token = match.Value;
            if (token.Length < 3 || IgnoredTokens.Contains(token))
                continue;
            if (Regex.IsMatch(token, @"^(?:19|20)\d{2}$"))
                continue;
            return token;
        }
        return string.Empty;
    }

    private static string NormalizeRomanNumerals(string title)
    {
        var normalized = Regex.Replace(title.ToLowerInvariant(), @"\b(?:ii|iii|iv|vi|vii|viii|ix|x|v|i)\b", match =>
            match.Value switch
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
                _ => match.Value
            });
        return normalized;
    }

    private static string Normalize(string title)
    {
        var normalized = Regex.Replace(NormalizeRomanNumerals(title), @"[^\p{L}\p{N}]+", " ").Trim();
        return Regex.Replace(normalized, @"\s+", " ");
    }
}
