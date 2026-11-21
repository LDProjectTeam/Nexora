using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nexora.Core.Services
{
    public static class GameSearchQueryMatcher
    {
        private static readonly Regex WordRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

        private static readonly Dictionary<string, string[]> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        {
            ["tlou"] = new[] { "The Last of Us" },
            ["tlou1"] = new[] { "The Last of Us" },
            ["tlou2"] = new[] { "The Last of Us Part II" },
            ["tlouii"] = new[] { "The Last of Us Part II" },
            ["cod"] = new[] { "Call of Duty" },
            ["код"] = new[] { "Call of Duty" },
            ["колда"] = new[] { "Call of Duty" },
            ["коллофдьюти"] = new[] { "Call of Duty" },
            ["codmw"] = new[] { "Call of Duty Modern Warfare" },
            ["codmw2"] = new[] { "Call of Duty Modern Warfare 2" },
            ["codmw3"] = new[] { "Call of Duty Modern Warfare 3" },
            ["gta"] = new[] { "Grand Theft Auto" },
            ["гта"] = new[] { "Grand Theft Auto" },
            ["gta4"] = new[] { "Grand Theft Auto IV" },
            ["gtaiv"] = new[] { "Grand Theft Auto IV" },
            ["gta5"] = new[] { "Grand Theft Auto V" },
            ["gtav"] = new[] { "Grand Theft Auto V" },
            ["гта5"] = new[] { "Grand Theft Auto V" },
            ["гта4"] = new[] { "Grand Theft Auto IV" },
            ["rdr"] = new[] { "Red Dead Redemption" },
            ["рдр"] = new[] { "Red Dead Redemption" },
            ["rdr1"] = new[] { "Red Dead Redemption" },
            ["рдр1"] = new[] { "Red Dead Redemption" },
            ["rdr2"] = new[] { "Red Dead Redemption 2" },
            ["рдр2"] = new[] { "Red Dead Redemption 2" },
            ["gow"] = new[] { "God of War", "Gears of War" },
            ["gow3"] = new[] { "God of War III" },
            ["год оф вар"] = new[] { "God of War" },
            ["годофвар"] = new[] { "God of War" },
            ["ac"] = new[] { "Assassin's Creed" },
            ["ас"] = new[] { "Assassin's Creed" },
            ["ac2"] = new[] { "Assassin's Creed II" },
            ["ac3"] = new[] { "Assassin's Creed III" },
            ["ac4"] = new[] { "Assassin's Creed IV Black Flag" },
            ["ассасин"] = new[] { "Assassin's Creed" },
            ["ассассин"] = new[] { "Assassin's Creed" },
            ["re"] = new[] { "Resident Evil" },
            ["re2"] = new[] { "Resident Evil 2" },
            ["re3"] = new[] { "Resident Evil 3" },
            ["re4"] = new[] { "Resident Evil 4" },
            ["re7"] = new[] { "Resident Evil 7" },
            ["re8"] = new[] { "Resident Evil Village" },
            ["резидент"] = new[] { "Resident Evil" },
            ["резидентэвил"] = new[] { "Resident Evil" },
            ["dbd"] = new[] { "Dead by Daylight" },
            ["bg3"] = new[] { "Baldur's Gate 3" },
            ["бг3"] = new[] { "Baldur's Gate 3" },
            ["bg"] = new[] { "Baldur's Gate" },
            ["бг"] = new[] { "Baldur's Gate" },
            ["балдурсгейт"] = new[] { "Baldur's Gate" },
            ["cp2077"] = new[] { "Cyberpunk 2077" },
            ["cyberpunk"] = new[] { "Cyberpunk 2077" },
            ["киберпанк"] = new[] { "Cyberpunk 2077" },
            ["киберпанк2077"] = new[] { "Cyberpunk 2077" },
            ["dmc"] = new[] { "Devil May Cry" },
            ["dmc5"] = new[] { "Devil May Cry 5" },
            ["mk"] = new[] { "Mortal Kombat" },
            ["мк"] = new[] { "Mortal Kombat" },
            ["mk1"] = new[] { "Mortal Kombat 1" },
            ["mk11"] = new[] { "Mortal Kombat 11" },
            ["mkx"] = new[] { "Mortal Kombat X" },
            ["морталкомбат"] = new[] { "Mortal Kombat" },
            ["fh"] = new[] { "Forza Horizon" },
            ["fh4"] = new[] { "Forza Horizon 4" },
            ["fh5"] = new[] { "Forza Horizon 5" },
            ["fh6"] = new[] { "Forza Horizon 6" },
            ["форза"] = new[] { "Forza" },
            ["fc"] = new[] { "Far Cry", "EA Sports FC" },
            ["fc24"] = new[] { "EA Sports FC 24" },
            ["fc25"] = new[] { "EA Sports FC 25" },
            ["fc3"] = new[] { "Far Cry 3" },
            ["fc4"] = new[] { "Far Cry 4" },
            ["fc5"] = new[] { "Far Cry 5" },
            ["fc6"] = new[] { "Far Cry 6" },
            ["фаркрай"] = new[] { "Far Cry" },
            ["фаркрай3"] = new[] { "Far Cry 3" },
            ["фаркрай4"] = new[] { "Far Cry 4" },
            ["фаркрай5"] = new[] { "Far Cry 5" },
            ["фаркрай6"] = new[] { "Far Cry 6" },
            ["ds"] = new[] { "Dark Souls" },
            ["дс"] = new[] { "Dark Souls" },
            ["ds1"] = new[] { "Dark Souls" },
            ["ds2"] = new[] { "Dark Souls II" },
            ["ds3"] = new[] { "Dark Souls III" },
            ["дс1"] = new[] { "Dark Souls" },
            ["дс2"] = new[] { "Dark Souls II" },
            ["дс3"] = new[] { "Dark Souls III" },
            ["дарксоулс"] = new[] { "Dark Souls" },
            ["hzd"] = new[] { "Horizon Zero Dawn" },
            ["hfw"] = new[] { "Horizon Forbidden West" },
            ["poe"] = new[] { "Path of Exile" },
            ["poe2"] = new[] { "Path of Exile 2" },
            ["cs2"] = new[] { "Counter-Strike 2" },
            ["кс2"] = new[] { "Counter-Strike 2" },
            ["cs"] = new[] { "Counter-Strike" },
            ["кс"] = new[] { "Counter-Strike" },
            ["ксго"] = new[] { "Counter-Strike Global Offensive" },
            ["csgo"] = new[] { "Counter-Strike Global Offensive" },
            ["nfs"] = new[] { "Need for Speed" },
            ["нфс"] = new[] { "Need for Speed" },
            ["nfsu"] = new[] { "Need for Speed Underground" },
            ["nfsu2"] = new[] { "Need for Speed Underground 2" },
            ["nfsmw"] = new[] { "Need for Speed Most Wanted" },
            ["нидфорспид"] = new[] { "Need for Speed" },
            ["tes"] = new[] { "The Elder Scrolls" },
            ["skyrim"] = new[] { "The Elder Scrolls V Skyrim" },
            ["скайрим"] = new[] { "The Elder Scrolls V Skyrim" },
            ["oblivion"] = new[] { "The Elder Scrolls IV Oblivion" },
            ["обливион"] = new[] { "The Elder Scrolls IV Oblivion" },
            ["morrowind"] = new[] { "The Elder Scrolls III Morrowind" },
            ["морровинд"] = new[] { "The Elder Scrolls III Morrowind" },
            ["me"] = new[] { "Mass Effect" },
            ["me1"] = new[] { "Mass Effect" },
            ["me2"] = new[] { "Mass Effect 2" },
            ["me3"] = new[] { "Mass Effect 3" },
            ["hl"] = new[] { "Half-Life" },
            ["hl1"] = new[] { "Half-Life" },
            ["hl2"] = new[] { "Half-Life 2" },
            ["халфа"] = new[] { "Half-Life" },
            ["халфлайф"] = new[] { "Half-Life" },
            ["witcher"] = new[] { "The Witcher" },
            ["witcher1"] = new[] { "The Witcher" },
            ["witcher2"] = new[] { "The Witcher 2 Assassins of Kings" },
            ["witcher3"] = new[] { "The Witcher 3 Wild Hunt" },
            ["ведьмак"] = new[] { "The Witcher" },
            ["ведьмак1"] = new[] { "The Witcher" },
            ["ведьмак2"] = new[] { "The Witcher 2 Assassins of Kings" },
            ["ведьмак3"] = new[] { "The Witcher 3 Wild Hunt" },
            ["bf"] = new[] { "Battlefield" },
            ["батла"] = new[] { "Battlefield" },
            ["батлфилд"] = new[] { "Battlefield" },
            ["bf1"] = new[] { "Battlefield 1" },
            ["bf3"] = new[] { "Battlefield 3" },
            ["bf4"] = new[] { "Battlefield 4" },
            ["bf5"] = new[] { "Battlefield V" },
            ["bf2042"] = new[] { "Battlefield 2042" },
            ["spiderman"] = new[] { "Spider-Man" },
            ["spiderman2"] = new[] { "Spider-Man 2" },
            ["человекпаук"] = new[] { "Spider-Man" },
            ["человекпаук2"] = new[] { "Spider-Man 2" },
            ["спайдермен"] = new[] { "Spider-Man" },
            ["eldenring"] = new[] { "Elden Ring" },
            ["елденринг"] = new[] { "Elden Ring" },
            ["элденринг"] = new[] { "Elden Ring" },
            ["fallout"] = new[] { "Fallout" },
            ["фоллаут"] = new[] { "Fallout" },
            ["stalker"] = new[] { "S.T.A.L.K.E.R." },
            ["stalker2"] = new[] { "S.T.A.L.K.E.R. 2 Heart of Chornobyl" },
            ["сталкер"] = new[] { "S.T.A.L.K.E.R." },
            ["сталкер2"] = new[] { "S.T.A.L.K.E.R. 2 Heart of Chornobyl" },
            ["metro"] = new[] { "Metro" },
            ["метро"] = new[] { "Metro" },
            ["fifa"] = new[] { "EA Sports FC", "FIFA" },
            ["фифа"] = new[] { "EA Sports FC", "FIFA" },
            ["sims"] = new[] { "The Sims" },
            ["симс"] = new[] { "The Sims" },
            ["silenthill"] = new[] { "Silent Hill" },
            ["сайлентхилл"] = new[] { "Silent Hill" },
            ["sh2"] = new[] { "Silent Hill 2" },
            ["сх2"] = new[] { "Silent Hill 2" },
            ["deadspace"] = new[] { "Dead Space" },
            ["дедспейс"] = new[] { "Dead Space" },
            ["дэдспэйс"] = new[] { "Dead Space" },
            ["alanwake"] = new[] { "Alan Wake" },
            ["alanwake2"] = new[] { "Alan Wake 2" },
            ["аланвейк"] = new[] { "Alan Wake" },
            ["аланвейк2"] = new[] { "Alan Wake 2" },
            ["mafia"] = new[] { "Mafia" },
            ["mafia1"] = new[] { "Mafia Definitive Edition" },
            ["mafia2"] = new[] { "Mafia II" },
            ["mafia3"] = new[] { "Mafia III" },
            ["мафия"] = new[] { "Mafia" },
            ["мафия1"] = new[] { "Mafia Definitive Edition" },
            ["мафия2"] = new[] { "Mafia II" },
            ["мафия3"] = new[] { "Mafia III" },
            ["dishonored"] = new[] { "Dishonored" },
            ["дизоноред"] = new[] { "Dishonored" },
            ["дисонорд"] = new[] { "Dishonored" },
            ["bioshock"] = new[] { "BioShock" },
            ["биошок"] = new[] { "BioShock" },
            ["dota"] = new[] { "Dota 2" },
            ["dota2"] = new[] { "Dota 2" },
            ["дота"] = new[] { "Dota 2" },
            ["дота2"] = new[] { "Dota 2" },
            ["warcraft"] = new[] { "Warcraft" },
            ["варкрафт"] = new[] { "Warcraft" },
            ["starcraft"] = new[] { "StarCraft" },
            ["старкрафт"] = new[] { "StarCraft" },
            ["diablo"] = new[] { "Diablo" },
            ["диабло"] = new[] { "Diablo" },
            ["диабло2"] = new[] { "Diablo II" },
            ["диабло4"] = new[] { "Diablo IV" },
            ["civ6"] = new[] { "Civilization VI" },
            ["цивилизация"] = new[] { "Civilization" },
            ["цива"] = new[] { "Civilization" },
            ["цива6"] = new[] { "Civilization VI" },
            ["homm3"] = new[] { "Heroes of Might and Magic III" },
            ["герои3"] = new[] { "Heroes of Might and Magic III" },
            ["герои"] = new[] { "Heroes of Might and Magic" },
            ["doom"] = new[] { "Doom" },
            ["дум"] = new[] { "Doom" },
            ["doometernal"] = new[] { "Doom Eternal" },
            ["думэтернал"] = new[] { "Doom Eternal" },
            ["sekiro"] = new[] { "Sekiro Shadows Die Twice" },
            ["секиро"] = new[] { "Sekiro Shadows Die Twice" },
            ["bloodborne"] = new[] { "Bloodborne" },
            ["бладборн"] = new[] { "Bloodborne" },
            ["deathstranding"] = new[] { "Death Stranding" },
            ["дезстрендинг"] = new[] { "Death Stranding" }
        };

        private static readonly Dictionary<char, char> EnToRuLayout = new()
        {
            ['q']='й', ['w']='ц', ['e']='у', ['r']='к', ['t']='е', ['y']='н', ['u']='г', ['i']='ш', ['o']='щ', ['p']='з',
            ['[']='х', [']']='ъ', ['a']='ф', ['s']='ы', ['d']='в', ['f']='а', ['g']='п', ['h']='р', ['j']='о', ['k']='л',
            ['l']='д', [';']='ж', ['\'']='э', ['z']='я', ['x']='ч', ['c']='с', ['v']='м', ['b']='и', ['n']='т', ['m']='ь',
            [',']='б', ['.']='ю', ['`']='ё'
        };

        private static readonly Dictionary<char, char> RuToEnLayout = new();

        static GameSearchQueryMatcher()
        {
            foreach (var kvp in EnToRuLayout)
            {
                if (!RuToEnLayout.ContainsKey(kvp.Value))
                    RuToEnLayout[kvp.Value] = kvp.Key;
            }
        }

        public static string ConvertKeyboardLayout(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            var chars = input.ToCharArray();
            var hasRu = input.Any(c => c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я' || c == 'ё' || c == 'Ё');
            for (int i = 0; i < chars.Length; i++)
            {
                var lower = char.ToLowerInvariant(chars[i]);
                if (hasRu)
                {
                    if (RuToEnLayout.TryGetValue(lower, out var enChar))
                        chars[i] = char.IsUpper(chars[i]) ? char.ToUpperInvariant(enChar) : enChar;
                }
                else
                {
                    if (EnToRuLayout.TryGetValue(lower, out var ruChar))
                        chars[i] = char.IsUpper(chars[i]) ? char.ToUpperInvariant(ruChar) : ruChar;
                }
            }

            return new string(chars);
        }

        public static bool MatchesTitleCharacters(string query, string title)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(title))
                return false;

            var queryKey = NormalizeKey(query);
            var titleKey = NormalizeKey(title);
            if (queryKey.Length == 0 || titleKey.Length == 0)
                return false;

            // 1. Direct key substring match
            if (titleKey.Contains(queryKey, StringComparison.Ordinal))
                return true;

            // Also check converted layout
            var convertedQuery = ConvertKeyboardLayout(query);
            var convertedQueryKey = NormalizeKey(convertedQuery);
            if (convertedQueryKey.Length > 0 && titleKey.Contains(convertedQueryKey, StringComparison.Ordinal))
                return true;

            // 2. Check abbreviations / aliases
            if (Abbreviations.TryGetValue(queryKey, out var expansions))
            {
                if (expansions.Any(exp => NormalizeKey(exp).Contains(titleKey, StringComparison.Ordinal) || titleKey.Contains(NormalizeKey(exp), StringComparison.Ordinal)))
                    return true;
            }
            if (convertedQueryKey.Length > 0 && Abbreviations.TryGetValue(convertedQueryKey, out var convExpansions))
            {
                if (convExpansions.Any(exp => NormalizeKey(exp).Contains(titleKey, StringComparison.Ordinal) || titleKey.Contains(NormalizeKey(exp), StringComparison.Ordinal)))
                    return true;
            }

            // Check if title words or title key have abbreviations matching query
            var words = WordRegex.Matches(title).Select(m => m.Value).ToArray();
            foreach (var w in words)
            {
                var wKey = NormalizeKey(w);
                if (Abbreviations.TryGetValue(wKey, out var wExpansions))
                {
                    if (wExpansions.Any(exp => NormalizeKey(exp).Contains(queryKey, StringComparison.Ordinal) || (convertedQueryKey.Length > 0 && NormalizeKey(exp).Contains(convertedQueryKey, StringComparison.Ordinal))))
                        return true;
                }
            }
            if (words.Length > 1)
            {
                var acronym = new string(words.Select(w => char.ToLowerInvariant(w[0])).ToArray());
                if (acronym.Contains(queryKey, StringComparison.Ordinal) || (convertedQueryKey.Length > 0 && acronym.Contains(convertedQueryKey, StringComparison.Ordinal)))
                    return true;

                // Also words without articles like "the", "of", "a", "an"
                var majorWords = words.Where(w => !w.Equals("the", StringComparison.OrdinalIgnoreCase)
                                               && !w.Equals("of", StringComparison.OrdinalIgnoreCase)
                                               && !w.Equals("a", StringComparison.OrdinalIgnoreCase)
                                               && !w.Equals("an", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (majorWords.Length > 1)
                {
                    var majorAcronym = new string(majorWords.Select(w => char.ToLowerInvariant(w[0])).ToArray());
                    if (majorAcronym.Contains(queryKey, StringComparison.Ordinal) || (convertedQueryKey.Length > 0 && majorAcronym.Contains(convertedQueryKey, StringComparison.Ordinal)))
                        return true;
                }
            }

            // 4. Word-by-word match: if query has multiple words, all must match some word in title
            var queryWords = WordRegex.Matches(query).Select(m => NormalizeKey(m.Value)).Where(w => w.Length > 0).ToArray();
            if (queryWords.Length > 1)
            {
                var titleWords = words.Select(NormalizeKey).Where(w => w.Length > 0).ToArray();
                if (queryWords.All(qw => titleWords.Any(tw => tw.Contains(qw, StringComparison.Ordinal))))
                    return true;
            }

            // 5. Word prefix match: query matches start of any word in title (e.g. "witche" -> "The Witcher")
            if (words.Any(w => NormalizeKey(w).StartsWith(queryKey, StringComparison.Ordinal)
                            || (convertedQueryKey.Length > 0 && NormalizeKey(w).StartsWith(convertedQueryKey, StringComparison.Ordinal))))
                return true;

            // 6. Subsequence matching only for abbreviations or compact queries with strict gap limits (max 3 gap chars between letters)
            // to prevent false positives like "war" matching unrelated titles across entire strings.
            if (queryKey.Length >= 3 && queryKey.Length <= 10)
            {
                if (MatchesWithCompactGaps(queryKey, titleKey, maxTotalGap: 4))
                    return true;
            }

            return false;
        }

        private static bool MatchesWithCompactGaps(string queryKey, string titleKey, int maxTotalGap)
        {
            var qIdx = 0;
            var firstMatchIdx = -1;
            var lastMatchIdx = -1;

            for (var tIdx = 0; tIdx < titleKey.Length; tIdx++)
            {
                if (titleKey[tIdx] == queryKey[qIdx])
                {
                    if (qIdx == 0) firstMatchIdx = tIdx;
                    lastMatchIdx = tIdx;
                    qIdx++;
                    if (qIdx == queryKey.Length)
                        break;
                }
            }

            if (qIdx == queryKey.Length)
            {
                var span = lastMatchIdx - firstMatchIdx + 1;
                var totalGap = span - queryKey.Length;
                return totalGap <= maxTotalGap;
            }

            return false;
        }

        public static IReadOnlyList<string> GetQueryAlternatives(
            string query,
            IEnumerable<string> knownTitles,
            bool allowFuzzyCorrection = true)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<string>();

            var trimmedQuery = query.Trim();
            var queryKey = NormalizeKey(trimmedQuery);
            if (queryKey.Length == 0)
                return Array.Empty<string>();

            var alternatives = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            seen.Add(trimmedQuery);

            // 1. Direct abbreviations for query
            if (Abbreviations.TryGetValue(queryKey, out var expansions))
            {
                foreach (var exp in expansions)
                {
                    if (seen.Add(exp))
                        alternatives.Add(exp);
                }
            }

            // 2. Keyboard layout converted query
            var converted = ConvertKeyboardLayout(trimmedQuery).Trim();
            var convertedKey = NormalizeKey(converted);
            if (convertedKey.Length > 0 && !string.Equals(converted, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                var hasRuInOriginal = trimmedQuery.Any(c => c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я' || c == 'ё' || c == 'Ё');

                // Check if layout-converted string has abbreviation expansions (e.g. "сщв" -> "cod" -> "Call of Duty")
                if (Abbreviations.TryGetValue(convertedKey, out var convExpansions))
                {
                    foreach (var exp in convExpansions)
                    {
                        if (seen.Add(exp))
                            alternatives.Add(exp);
                    }
                }

                // If original was Russian (e.g. "сщв" -> "cod", "еру цшесрук" -> "the witcher"),
                // the Latin conversion is always a viable search alternative.
                // If original was Latin, only add Cyrillic conversion if it matches an alias or known title.
                if (hasRuInOriginal || Abbreviations.ContainsKey(convertedKey))
                {
                    if (seen.Add(converted))
                        alternatives.Add(converted);
                }
            }

            // 3. Multi-word phrase expansions (e.g. "ведьмак 3" -> "The Witcher 3")
            var words = WordRegex.Matches(trimmedQuery).Select(m => m.Value).ToArray();
            if (words.Length > 1)
            {
                for (int i = 0; i < words.Length; i++)
                {
                    var wordKey = NormalizeKey(words[i]);
                    if (Abbreviations.TryGetValue(wordKey, out var wordExpansions))
                    {
                        foreach (var we in wordExpansions)
                        {
                            var replacedWords = (string[])words.Clone();
                            replacedWords[i] = we;
                            var phrase = string.Join(" ", replacedWords);
                            if (seen.Add(phrase))
                                alternatives.Add(phrase);
                        }
                    }
                }
            }

            // 3.5. Base franchise / game title extraction (stripping subtitle, edition, bundle, or repack keywords)
            // e.g. "The Long Dark: Quiet Apocalypse Edition" -> "The Long Dark"
            var colonIdx = trimmedQuery.IndexOfAny(new[] { ':', '—', '–' });
            if (colonIdx > 2)
            {
                var basePart = trimmedQuery.Substring(0, colonIdx).Trim();
                if (basePart.Length >= 3 && seen.Add(basePart))
                    alternatives.Add(basePart);
            }
            var dashIdx = trimmedQuery.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 2)
            {
                var basePart = trimmedQuery.Substring(0, dashIdx).Trim();
                if (basePart.Length >= 3 && seen.Add(basePart))
                    alternatives.Add(basePart);
            }
            var editionStripped = Regex.Replace(
                trimmedQuery,
                @"\b(Edition|Bundle|Remastered|Definitive|Collection|Anthology|Game of the Year|GOTY|Complete)\b.*$",
                "",
                RegexOptions.IgnoreCase).Trim(' ', ':', '-', '—', '–');
            if (editionStripped.Length >= 3 && !string.Equals(editionStripped, trimmedQuery, StringComparison.OrdinalIgnoreCase) && seen.Add(editionStripped))
            {
                alternatives.Add(editionStripped);
            }

            // 4. Fuzzy typo correction against known indexed titles if enabled
            if (allowFuzzyCorrection && (queryKey.Length >= 4 || convertedKey.Length >= 4))
            {
                var titles = knownTitles
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (titles.Length > 0)
                {
                    var fuzzy = FindFuzzyCorrections(queryKey, titles);
                    if (fuzzy.Count == 0 && convertedKey.Length >= 4)
                        fuzzy = FindFuzzyCorrections(convertedKey, titles);

                    foreach (var f in fuzzy)
                    {
                        if (seen.Add(f))
                            alternatives.Add(f);
                    }
                }
            }

            return alternatives;
        }

        private static IReadOnlyList<string> FindFuzzyCorrections(string queryKey, string[] titles)
        {
            if (titles.Any(title => NormalizeKey(title) == queryKey))
                return Array.Empty<string>();

            var maxDistance = queryKey.Length <= 6 ? 1 : queryKey.Length <= 12 ? 2 : 3;
            var matches = new List<(string Title, int Distance)>();
            foreach (var title in titles)
            {
                var titleKey = NormalizeKey(title);
                if (titleKey.Length < 4 || titleKey == queryKey)
                    continue;

                var distance = GetTitleDistance(queryKey, title);
                if (distance <= maxDistance)
                    matches.Add((title, distance));
            }

            if (matches.Count == 0)
                return Array.Empty<string>();

            var bestDistance = matches.Min(match => match.Distance);
            var bestTitles = matches
                .Where(match => match.Distance == bestDistance)
                .GroupBy(match => NormalizeKey(match.Title), StringComparer.Ordinal)
                .Select(group => group.First().Title)
                .Take(2)
                .ToList();

            // Avoid silently correcting a typo when two different titles are equally plausible.
            return bestTitles.Count == 1 ? bestTitles : Array.Empty<string>();
        }

        private static int GetTitleDistance(string queryKey, string title)
        {
            var words = WordRegex.Matches(title).Select(match => match.Value.ToLowerInvariant()).ToArray();
            if (words.Length == 0)
                return int.MaxValue;

            var bestDistance = EditDistance(queryKey, NormalizeKey(title));
            for (var start = 0; start < words.Length; start++)
            {
                var phrase = string.Empty;
                for (var length = 1; length <= 5 && start + length <= words.Length; length++)
                {
                    phrase += words[start + length - 1];
                    if (Math.Abs(queryKey.Length - phrase.Length) > bestDistance)
                        continue;
                    bestDistance = Math.Min(bestDistance, EditDistance(queryKey, phrase));
                    if (bestDistance == 0)
                        return 0;
                }
            }

            return bestDistance;
        }

        private static int EditDistance(string left, string right)
        {
            if (left.Length == 0)
                return right.Length;
            if (right.Length == 0)
                return left.Length;
            if (Math.Abs(left.Length - right.Length) > 3)
                return Math.Abs(left.Length - right.Length);

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var column = 0; column <= right.Length; column++)
                previous[column] = column;

            for (var row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                var rowMinimum = row;
                for (var column = 1; column <= right.Length; column++)
                {
                    var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + cost);
                    rowMinimum = Math.Min(rowMinimum, current[column]);
                }

                if (rowMinimum > 3)
                    return rowMinimum;
                (previous, current) = (current, previous);
            }

            return previous[right.Length];
        }

        private static string NormalizeKey(string value)
            => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
