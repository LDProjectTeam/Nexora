package artworksearch

import (
	"regexp"
	"strconv"
	"strings"
)

var wordRE = regexp.MustCompile(`[\pL\pN]+`)
var blackOpsEndRE = regexp.MustCompile(`\bblack\s+ops$`)
var identityBracketRE = regexp.MustCompile(`\[[^\]]*\]`)
var identityYearRE = regexp.MustCompile(`(?i)\((?:19|20)\d{2}(?:\s*[-–]\s*(?:19|20)?\d{2})?\)`)
var identityVersionRE = regexp.MustCompile(`(?i)\b(?:v|ver|version|build)\s*[0-9][\w.\-]*`)
var identityReleaseNoiseRE = regexp.MustCompile(`(?i)\b(?:pc|portable|repack|repacked|fitgirl|dodi|rutor|gog|scene|torrent)\b.*$`)
var ignored = map[string]bool{}
var ignoredTitleWords = map[string]bool{
	"the": true, "a": true, "an": true, "and": true, "of": true, "in": true, "on": true,
	"for": true, "to": true, "with": true, "by": true, "at": true, "from": true,
	"edition": true, "bundle": true, "complete": true, "deluxe": true, "gold": true,
	"premium": true, "digital": true, "standard": true, "anniversary": true,
	"collector": true, "collectors": true, "goty": true, "soundtrack": true,
	"repack": true, "repacked": true, "portable": true, "rip": true, "installer": true,
	"setup": true, "fitgirl": true, "dodi": true, "rutor": true, "gog": true,
	"steamrip": true, "pc": true, "rus": true, "eng": true, "windows": true,
	"marvel": true, "marvels": true,
	"cover": true, "covers": true, "box": true, "boxart": true, "boxshot": true,
	"art": true, "artwork": true, "dilogy": true, "trilogy": true, "collection": true,
}

func init() {
	for _, w := range strings.Fields("the a an and of in on for to with by at from game year goty edition bundle complete set ultimate definitive praetor centurion maximum deluxe gold premium reloaded enhanced remastered digital standard anniversary collector collectors repack portable rip directors director cut patch update v build multi rus eng steamrip dodi fitgirl gog pc all dlc dlcs soundtrack artbook pass season expansions expansion sid meier meiers tom clancy clancys marvel marvels cover covers box boxart boxshot art artwork dilogy trilogy collection") {
		ignored[w] = true
	}
}

func Normalize(s string) string {
	return strings.Join(wordRE.FindAllString(strings.ToLower(s), -1), " ")
}

func tokens(title string) map[string]bool {
	n := Normalize(identityText(title))
	out := map[string]bool{}
	for _, w := range wordRE.FindAllString(n, -1) {
		w = roman(w)
		if !ignored[w] {
			out[w] = true
		}
	}
	if blackOpsEndRE.MatchString(n) {
		out["1"] = true
	}
	return out
}

func roman(s string) string {
	if n, ok := map[string]string{"i": "1", "ii": "2", "iii": "3", "iv": "4", "v": "5", "vi": "6", "vii": "7", "viii": "8", "ix": "9", "x": "10"}[s]; ok {
		return n
	}
	return s
}

func MatchScore(query, candidate string) float64 {
	a, b := tokens(query), tokens(candidate)
	if len(a) == 0 || len(b) == 0 {
		return 0
	}
	if !SameInstallments(query, candidate) {
		return 0
	}
	primary := ""
	for _, w := range wordRE.FindAllString(Normalize(query), -1) {
		if len([]rune(w)) >= 3 && !ignored[w] {
			primary = roman(w)
			break
		}
	}
	if primary != "" && !b[primary] {
		return 0
	}
	if same(a, b) {
		return 1
	}
	common := 0
	queryContained := true
	for w := range a {
		if b[w] {
			common++
		} else {
			queryContained = false
		}
	}
	if queryContained {
		return .75
	}
	return float64(common) / float64(max(len(a), len(b)))
}

// installmentMarkers captures named branches that are often treated as subtitles
// by generic image search (for example Black Ops Cold War).
func installmentMarkers(title string) map[string]bool {
	n := Normalize(identityText(title))
	n = strings.ReplaceAll(n, "marvel s ", "marvel ")
	markers := map[string]bool{}
	for _, marker := range []string{
		"cold war", "modern warfare", "black ops", "infinite warfare", "advanced warfare", "world at war", "zombies chronicles",
		"ultimate spider man", "miles morales", "web of shadows", "shattered dimensions", "edge of time", "friend or foe",
		"amazing spider man", "marvel spider man", "spider man remastered", "spider man the movie",
		"spider man 2 the game", "spider man 3 the game",
	} {
		if strings.Contains(" "+n+" ", " "+marker+" ") {
			markers[marker] = true
		}
	}
	return markers
}

func SameInstallments(query, candidate string) bool {
	if isMetroReduxCollection(query) && isMetroReduxGame(candidate) {
		return true
	}
	if !same(installmentMarkers(query), installmentMarkers(candidate)) {
		return false
	}
	if hasConflictingSubtitle(query, candidate) {
		return false
	}
	na, nb := map[string]bool{}, map[string]bool{}
	for w := range tokens(query) {
		if n, e := strconv.Atoi(w); e == nil && n >= 1 && n <= 99 {
			na[w] = true
		}
	}
	for w := range tokens(candidate) {
		if n, e := strconv.Atoi(w); e == nil && n >= 1 && n <= 99 {
			nb[w] = true
		}
	}
	return same(na, nb)
}

func isMetroReduxCollection(title string) bool {
	n := Normalize(identityText(title))
	return strings.Contains(n, "metro") && strings.Contains(n, "redux") &&
		(regexp.MustCompile(`\b(dilogy|collection|bundle|double\s+pack)\b`).MatchString(n))
}

func isMetroReduxGame(title string) bool {
	n := Normalize(identityText(title))
	return strings.Contains(n, "metro") && strings.Contains(n, "redux") &&
		(strings.Contains(n, "2033") || strings.Contains(n, "last light"))
}

// If two titles share their recognizable base but each has its own extra title
// words, those words are competing subtitles. Do not treat that as a fuzzy match.
// A one-sided extension (e.g. "The Witcher 3" / "The Witcher III Wild Hunt")
// remains acceptable.
func hasConflictingSubtitle(a, b string) bool {
	left, right := titleIdentityTokens(a), titleIdentityTokens(b)
	common, leftOnly, rightOnly := 0, 0, 0
	for token := range left {
		if right[token] {
			common++
		} else {
			leftOnly++
		}
	}
	for token := range right {
		if !left[token] {
			rightOnly++
		}
	}
	return common >= 1 && leftOnly > 0 && rightOnly > 0
}

func titleIdentityTokens(title string) map[string]bool {
	out := map[string]bool{}
	for _, word := range wordRE.FindAllString(Normalize(identityText(title)), -1) {
		word = roman(word)
		if !ignoredTitleWords[word] {
			out[word] = true
		}
	}
	return out
}

// Remove release metadata before comparing game identity. In particular, a
// repack version like "[v 2.5]" must never be mistaken for a sequel number.
func identityText(title string) string {
	clean := strings.Split(title, "|")[0]
	clean = identityBracketRE.ReplaceAllString(clean, " ")
	clean = identityYearRE.ReplaceAllString(clean, " ")
	clean = identityVersionRE.ReplaceAllString(clean, " ")
	clean = identityReleaseNoiseRE.ReplaceAllString(clean, " ")
	return strings.TrimSpace(clean)
}
func same(a, b map[string]bool) bool {
	if len(a) != len(b) {
		return false
	}
	for k := range a {
		if !b[k] {
			return false
		}
	}
	return true
}

func SearchQueries(title string) []string {
	clean := strings.Split(title, "|")[0]
	clean = regexp.MustCompile(`\[[^\]]*\]|\([^)]*\)`).ReplaceAllString(clean, " ")
	// Do not strip four-digit numbers here: they can be part of a real title (Cohen 1939).
	// Release years are usually removed with their surrounding parentheses above or by
	// the later PC/repack marker cleanup.
	clean = regexp.MustCompile(`(?i)\b(?:v\s*\d[\w.]*|pc|portable|repack|repacked|fitgirl|dodi|rutor|gog|scene|torrent)\b.*$`).ReplaceAllString(clean, " ")
	clean = strings.TrimSpace(strings.Trim(clean, " -:|"))
	dlc := regexp.MustCompile(`(?i)(?:\s*[-:&+] |\s+with |\s+including )?(?:all\s+)?(?:dlc'?s?|soundtrack|artbook|expansions?|season\s+pass)(?:\s+bundle|\s+edition)?\b.*$`).ReplaceAllString(clean, "")
	core := regexp.MustCompile(`(?i)(?:\s*[-:|]?\s*)(?:director'?s?\s+cut|complete(?:\s+the\s+set)?|ultimate|definitive|maximum|deluxe|gold|premium|praetor|centurion|reloaded|enhanced|remastered|digital|standard|anniversary|collector'?s?|edition|bundle)(?:\s+edition)?\s*$`).ReplaceAllString(clean, "")
	stripped := regexp.MustCompile(`(?i)\b(complete\s+the\s+set|director'?s?\s+cut|ultimate|definitive|maximum|deluxe|gold|premium|praetor|centurion|reloaded|enhanced|remastered|digital|standard|anniversary|collector'?s?|edition|bundle|repack|portable)\b`).ReplaceAllString(clean, " ")
	stripped = regexp.MustCompile(`\s+`).ReplaceAllString(stripped, " ")
	stripped = strings.TrimSpace(strings.Trim(stripped, " -:|"))
	compact := clean
	compactRE := regexp.MustCompile(`([\pL])\.([\pL])`)
	for {
		next := compactRE.ReplaceAllString(compact, "$1$2")
		if next == compact {
			break
		}
		compact = next
	}
	queries := []string{clean, strings.TrimSpace(dlc), strings.TrimSpace(strings.Trim(core, " -:|")), stripped, compact}
	seen := map[string]bool{}
	out := []string{}
	for _, q := range queries {
		q = strings.TrimSpace(q)
		k := strings.ToLower(q)
		if len([]rune(q)) >= 2 && !seen[k] {
			seen[k] = true
			out = append(out, q)
		}
	}
	broadRE := regexp.MustCompile(`^(.{3,}?)\s*(?::|\s[-–—]\s)`)
	mainQueries := append([]string{}, out...)
	for _, q := range mainQueries {
		if match := broadRE.FindStringSubmatch(q); len(match) > 1 {
			base := strings.TrimSpace(match[1])
			key := strings.ToLower(base)
			if len([]rune(base)) >= 3 && !seen[key] {
				seen[key] = true
				out = append(out, base)
			}
		}
	}
	return out
}

// ProgressiveSearchQueries drops one trailing word at a time from the cleaned
// title. These broader searches are only used after the exact Steam searches fail.
func ProgressiveSearchQueries(title string) []string {
	return ProgressiveQueriesFrom(SearchQueries(title))
}

func ProgressiveQueriesFrom(base []string) []string {
	if len(base) == 0 {
		return nil
	}
	words := wordRE.FindAllString(base[0], -1)
	seen := map[string]bool{}
	for _, query := range base {
		seen[strings.ToLower(query)] = true
	}
	var out []string
	for len(words) > 2 {
		words = words[:len(words)-1]
		query := strings.Join(words, " ")
		key := strings.ToLower(query)
		if !seen[key] {
			seen[key] = true
			out = append(out, query)
		}
	}
	return out
}
