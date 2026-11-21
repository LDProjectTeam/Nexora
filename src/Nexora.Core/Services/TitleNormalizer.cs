using System;
using System.Text.RegularExpressions;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    public class TitleNormalizer : ITitleNormalizer
    {
        private static readonly Regex YearRegex = new(@"\b(19\d\d|20\d\d)\b", RegexOptions.Compiled);
        private static readonly Regex VersionRegex = new(@"(?:\[|\(|\b)(?:v|ver|версия|build)[\s\.:]*([0-9]+(?:\.[0-9a-zA-Z_\-]+)*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AuthorRegex = new(@"(?:от|by)\s+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PortableRegex = new(
            @"\bportable\b|\bno[\s-]?install\b|\bpre[\s-]?installed\b|\bstand[\s-]?alone\b|портативн\w*|без\s+(?:установки|инсталляции)|безустановочн\w*|распакованн\w*|не\s+требует\s+установки|готов\w*\s+к\s+запуску",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public NormalizedTitleInfo Normalize(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
                return new NormalizedTitleInfo();

            var info = new NormalizedTitleInfo();

            // 1. Detect Version Type
            var lower = rawTitle.ToLowerInvariant();
            if (PortableRegex.IsMatch(rawTitle))
            {
                info.VersionType = GameVersionType.Portable;
            }
            else if (lower.Contains("steam-rip") || lower.Contains("steamrip") ||
                     lower.Contains("installer") || lower.Contains("setup") || lower.Contains("gog") ||
                     lower.Contains("лицензия"))
            {
                info.VersionType = GameVersionType.Installer;
            }
            else if (lower.Contains("repack") || lower.Contains("репак") ||
                     lower.Contains("rip от") || lower.Contains("рип от") ||
                     Regex.IsMatch(lower, @"\b(rip|рип)\b"))
            {
                info.VersionType = GameVersionType.Repack;
            }
            else if (lower.Contains(".zip") || lower.Contains(".7z") || lower.Contains(".rar") || lower.Contains("архив"))
            {
                info.VersionType = GameVersionType.Archive;
            }
            else
            {
                info.VersionType = GameVersionType.Unknown;
            }

            // 2. Extract Author
            var authorMatch = AuthorRegex.Match(rawTitle);
            if (authorMatch.Success)
            {
                info.ReleaseAuthor = authorMatch.Groups[1].Value.Trim();
            }
            else if (lower.Contains("gog"))
            {
                info.ReleaseAuthor = "GOG";
            }

            // 3. Extract Version
            var verMatch = VersionRegex.Match(rawTitle);
            if (verMatch.Success)
            {
                info.Version = verMatch.Groups[1].Value.Trim();
            }

            // 4. Extract Year
            var yearMatch = YearRegex.Match(rawTitle);
            if (yearMatch.Success)
            {
                info.Year = yearMatch.Groups[1].Value;
            }

            // 5. Clean / Canonical Title & Alt Title
            // Many tracker titles are formatted: "English Title / Russian Title (Year) PC | Type"
            var parts = rawTitle.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            var mainPart = parts.Length > 0 ? parts[0] : rawTitle;

            // Remove trailing brackets like (2015) PC, [v 1.32], etc.
            var bracketIndex = mainPart.IndexOfAny(new[] { '(', '[' });
            var titleSegment = bracketIndex > 0 ? mainPart.Substring(0, bracketIndex) : mainPart;

            var slashParts = titleSegment.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (slashParts.Length >= 2)
            {
                info.CleanTitle = CleanSegment(slashParts[0]);
                info.AltTitle = CleanSegment(slashParts[1]);
            }
            else if (slashParts.Length == 1)
            {
                info.CleanTitle = CleanSegment(slashParts[0]);
            }
            else
            {
                info.CleanTitle = CleanSegment(rawTitle);
            }

            return info;
        }

        public bool AreTitlesMatching(string titleA, string titleB)
        {
            if (string.IsNullOrWhiteSpace(titleA) || string.IsNullOrWhiteSpace(titleB))
                return false;

            var normA = NormalizeKey(titleA);
            var normB = NormalizeKey(titleB);

            if (normA == normB) return true;

            // Check if one contains the other as complete words
            if (normA.Length > 3 && normB.Length > 3)
            {
                if (normA.Contains(normB) || normB.Contains(normA))
                    return true;
            }

            return false;
        }

        private static string CleanSegment(string text)
        {
            text = Regex.Replace(text, @"\b(PC|RePack|Portable|GOG|Rip)\b", "", RegexOptions.IgnoreCase);
            return text.Trim(' ', '-', ':', '/', '|', '[', ']', '(', ')');
        }

        private static string NormalizeKey(string text)
        {
            var cleaned = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
            return cleaned.Trim();
        }
    }
}
