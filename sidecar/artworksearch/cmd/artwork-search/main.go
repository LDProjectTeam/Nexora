package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"image"
	_ "image/gif"
	_ "image/jpeg"
	_ "image/png"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	art "nexora/artwork-search"
)

type request struct {
	Version        int    `json:"version"`
	GameTitle      string `json:"gameTitle"`
	ReleasePageURL string `json:"releasePageUrl,omitempty"`
	CachePath      string `json:"cachePath"`
}
type artwork struct {
	CoverPath           string `json:"CoverPath"`
	BannerPath          string `json:"BannerPath"`
	OfficialReleaseDate string `json:"OfficialReleaseDate"`
}
type storeItem struct {
	ID        int    `json:"id"`
	Name      string `json:"name"`
	TinyImage string `json:"tiny_image"`
	Type      string `json:"type"`
}
type appDetails struct {
	Success bool `json:"success"`
	Data    struct {
		Type        string `json:"type"`
		HeaderImage string `json:"header_image"`
		ReleaseDate struct {
			Date string `json:"date"`
		} `json:"release_date"`
		BackgroundRaw string `json:"background_raw"`
		Background    string `json:"background"`
	} `json:"data"`
}
type page struct {
	Title     string `json:"title"`
	Thumbnail struct {
		URL string `json:"url"`
	} `json:"thumbnail"`
}

var client = newSafeClient()
var attrRE = regexp.MustCompile(`(?is)([\w:-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')`)
var tagRE = regexp.MustCompile(`(?is)<(?:meta|img|a)\b[^>]*>`)
var anchorRE = regexp.MustCompile(`(?is)<a\b[^>]*>.*?</a>`)
var appRE = regexp.MustCompile(`/app/(\d+)`)
var titleRE = regexp.MustCompile(`(?is)<title\b[^>]*>(.*?)</title>`)
var deniedCIDRs = func() []*net.IPNet {
	blocks := []*net.IPNet{}
	for _, block := range []string{"100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4", "2001:db8::/32"} {
		_, network, _ := net.ParseCIDR(block)
		blocks = append(blocks, network)
	}
	return blocks
}()

func main() {
	dec := json.NewDecoder(os.Stdin)
	var in request
	if err := dec.Decode(&in); err != nil {
		fail("invalid request: %v", err)
		return
	}
	if in.Version != 1 || strings.TrimSpace(in.GameTitle) == "" || strings.TrimSpace(in.CachePath) == "" {
		fail("unsupported protocol version or missing fields")
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 70*time.Second)
	defer cancel()
	out, err := find(ctx, in)
	if err != nil {
		fail("artwork lookup: %v", err)
		out = nil
	}
	enc := json.NewEncoder(os.Stdout)
	if err := enc.Encode(out); err != nil {
		fmt.Fprintln(os.Stderr, "protocol response:", err)
	}
}
func fail(format string, a ...any) { fmt.Fprintf(os.Stderr, format+"\n", a...) }

func find(ctx context.Context, in request) (*artwork, error) {
	exactQueries := art.SearchQueries(in.GameTitle)
	if len(exactQueries) == 0 {
		return nil, nil
	}
	progressiveQueries := art.ProgressiveQueriesFrom(exactQueries)
	queries := []string{exactQueries[0]}
	queries = append(queries, progressiveQueries...)
	queries = append(queries, exactQueries[1:]...)
	identity := "artwork-v8|" + strings.ToLower(art.Normalize(in.GameTitle)+"|"+in.ReleasePageURL)
	sum := sha256.Sum256([]byte(identity))
	key := hex.EncodeToString(sum[:])
	if err := os.MkdirAll(in.CachePath, 0755); err != nil {
		return nil, err
	}
	ctx = context.WithValue(ctx, cacheKeyType{}, in.CachePath)
	meta := filepath.Join(in.CachePath, key+"_go_v8.json")
	legacyIdentity := strings.ToLower(art.Normalize(in.GameTitle) + "|en")
	legacySum := sha256.Sum256([]byte(legacyIdentity))
	legacySuffix := "_v19"
	if u, err := url.Parse(in.ReleasePageURL); err == nil && strings.Contains(strings.ToLower(u.Hostname()), "rutor") {
		legacySuffix = "_rutor_v19"
	}
	legacyMeta := filepath.Join(in.CachePath, hex.EncodeToString(legacySum[:])+legacySuffix+".json")
	if b, err := os.ReadFile(legacyMeta); err == nil {
		var cached artwork
		if json.Unmarshal(b, &cached) == nil && validCached(cached) {
			return &cached, nil
		}
	}
	if b, e := os.ReadFile(meta); e == nil {
		var cached artwork
		if json.Unmarshal(b, &cached) == nil && validCached(cached) {
			return &cached, nil
		}
	}
	if fi, e := os.Stat(meta + ".miss"); e == nil && time.Since(fi.ModTime()) < time.Minute {
		return nil, nil
	}
	// Bing is independent of Steam, so start the fallback while Steam is being searched.
	// Steam still wins whenever it returns a verified game match.
	bingCtx, bingCancel := context.WithCancel(ctx)
	defer bingCancel()
	bingResult := make(chan *artwork, 1)
	go func() { bingResult <- bingArtwork(bingCtx, in.GameTitle, queries, key) }()
	// Search multiple cleaned forms; each failed source/query is isolated.
	items := []storeItem{}
	seen := map[int]bool{}
	searchCtx, cancel := context.WithTimeout(ctx, 9*time.Second)
	defer cancel()
	searchResults := steamSearchMany(searchCtx, exactQueries)
	for _, found := range searchResults {
		for _, it := range found {
			if !seen[it.ID] && it.ID > 0 && isStoreAppCandidate(it.Type) && art.SameInstallments(in.GameTitle, it.Name) {
				seen[it.ID] = true
				items = append(items, it)
			}
		}
	}
	type ranked struct {
		item  storeItem
		score float64
	}
	ranks := []ranked{}
	for _, it := range items {
		best := 0.0
		for _, q := range queries {
			if s := art.MatchScore(q, it.Name); s > best {
				best = s
			}
		}
		if best >= .66 {
			ranks = append(ranks, ranked{it, best})
		}
	}
	sort.SliceStable(ranks, func(i, j int) bool { return ranks[i].score > ranks[j].score })
	for _, r := range ranks {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		default:
		}
		got, e := steamArtwork(ctx, r.item)
		if e != nil {
			continue
		}
		if got != nil {
			save(meta, *got)
			save(legacyMeta, *got)
			return got, nil
		}
	}
	// Exact/edition-cleaned names get first priority. If those fail, progressively
	// shorten the title one trailing word at a time, stopping at the first usable art.
	progressCtx, progressCancel := context.WithTimeout(ctx, 10*time.Second)
	for _, query := range progressiveQueries {
		queryCtx, queryCancel := context.WithTimeout(progressCtx, 2500*time.Millisecond)
		found, searchErr := steamSearch(queryCtx, query)
		queryCancel()
		if searchErr != nil {
			if progressCtx.Err() != nil {
				break
			}
			continue
		}
		candidates := make([]ranked, 0, len(found))
		for _, it := range found {
			if it.ID <= 0 || seen[it.ID] || !isStoreAppCandidate(it.Type) || !art.SameInstallments(in.GameTitle, it.Name) {
				continue
			}
			seen[it.ID] = true
			if score := art.MatchScore(query, it.Name); score >= .66 {
				candidates = append(candidates, ranked{it, score})
			}
		}
		sort.SliceStable(candidates, func(i, j int) bool { return candidates[i].score > candidates[j].score })
		for _, candidate := range candidates {
			got, imageErr := steamArtwork(progressCtx, candidate.item)
			if imageErr == nil && got != nil {
				save(meta, *got)
				save(legacyMeta, *got)
				progressCancel()
				return got, nil
			}
		}
		if progressCtx.Err() != nil {
			break
		}
	}
	progressCancel()
	// The Steam search page is the same useful fallback as the former C# service.
	pageCtx, pageCancel := context.WithTimeout(ctx, 8*time.Second)
	pageResults := steamPageSearchMany(pageCtx, queries[:min(2, len(queries))])
	pageCancel()
	pageRanks := []ranked{}
	for _, found := range pageResults {
		for _, it := range found {
			if seen[it.ID] || it.ID <= 0 || !art.SameInstallments(in.GameTitle, it.Name) {
				continue
			}
			seen[it.ID] = true
			best := 0.0
			for _, q := range queries {
				if score := art.MatchScore(q, it.Name); score > best {
					best = score
				}
			}
			if best >= .66 {
				pageRanks = append(pageRanks, ranked{it, best})
			}
		}
	}
	sort.SliceStable(pageRanks, func(i, j int) bool { return pageRanks[i].score > pageRanks[j].score })
	for _, r := range pageRanks {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		default:
		}
		got, err := steamArtwork(ctx, r.item)
		if err == nil && got != nil {
			save(meta, *got)
			save(legacyMeta, *got)
			return got, nil
		}
	}
	// Legacy known alias retained for the existing release-title case.
	if strings.HasPrefix(art.Normalize(in.GameTitle), "dying light the beast") && !seen[3008130] {
		if got, e := steamArtwork(ctx, storeItem{ID: 3008130, Name: in.GameTitle, Type: "game"}); e == nil && got != nil {
			save(meta, *got)
			save(legacyMeta, *got)
			return got, nil
		}
	}
	if got := releaseArtwork(ctx, in.GameTitle, in.ReleasePageURL, key); got != nil {
		save(meta, *got)
		save(legacyMeta, *got)
		return got, nil
	}
	if got := lutrisArtwork(ctx, in.GameTitle, queries, key); got != nil {
		save(meta, *got)
		save(legacyMeta, *got)
		return got, nil
	}
	if got := wikiArtwork(ctx, in.GameTitle, queries, key); got != nil {
		save(meta, *got)
		save(legacyMeta, *got)
		return got, nil
	}
	select {
	case got := <-bingResult:
		if got != nil {
			save(meta, *got)
			save(legacyMeta, *got)
			return got, nil
		}
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	_ = os.WriteFile(meta+".miss", []byte(time.Now().UTC().Format(time.RFC3339)), 0644)
	return nil, nil
}

func get(ctx context.Context, raw string, limit int64) ([]byte, http.Header, error) {
	u, e := url.Parse(raw)
	if e != nil || !safeURL(ctx, u) {
		return nil, nil, errors.New("unsafe URL")
	}
	req, e := http.NewRequestWithContext(ctx, "GET", u.String(), nil)
	if e != nil {
		return nil, nil, e
	}
	req.Header.Set("User-Agent", "Nexora/1.0 artwork-search")
	req.Header.Set("Accept", "image/avif,image/webp,image/*,application/json,text/html;q=0.9,*/*;q=0.8")
	r, e := client.Do(req)
	if e != nil {
		return nil, nil, e
	}
	defer r.Body.Close()
	if r.StatusCode < 200 || r.StatusCode >= 300 {
		return nil, r.Header, fmt.Errorf("HTTP %d", r.StatusCode)
	}
	if r.ContentLength > limit {
		return nil, r.Header, errors.New("response too large")
	}
	b, e := io.ReadAll(io.LimitReader(r.Body, limit+1))
	if e != nil {
		return nil, r.Header, e
	}
	if int64(len(b)) > limit {
		return nil, r.Header, errors.New("response too large")
	}
	return b, r.Header, nil
}
func safeURL(ctx context.Context, u *url.URL) bool {
	if u == nil || u.Scheme != "https" || u.User != nil || (u.Port() != "" && u.Port() != "443") {
		return false
	}
	host := strings.TrimSuffix(strings.ToLower(u.Hostname()), ".")
	if host == "" || host == "localhost" || strings.HasSuffix(host, ".localhost") || strings.HasSuffix(host, ".local") {
		return false
	}
	if ip := net.ParseIP(host); ip != nil {
		return publicIP(ip)
	}
	ips, e := net.DefaultResolver.LookupIPAddr(ctx, host)
	if e != nil || len(ips) == 0 {
		return false
	}
	for _, ip := range ips {
		if !publicIP(ip.IP) {
			return false
		}
	}
	return true
}
func publicIP(ip net.IP) bool {
	if ip == nil || !ip.IsGlobalUnicast() || ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || ip.IsUnspecified() || ip.IsMulticast() {
		return false
	}
	for _, block := range deniedCIDRs {
		if block.Contains(ip) {
			return false
		}
	}
	return true
}
func newSafeClient() *http.Client {
	d := &net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}
	tr := &http.Transport{TLSHandshakeTimeout: 5 * time.Second, ResponseHeaderTimeout: 8 * time.Second, IdleConnTimeout: 30 * time.Second, MaxIdleConns: 12, MaxIdleConnsPerHost: 3, DisableCompression: false}
	tr.DialContext = func(ctx context.Context, network, address string) (net.Conn, error) {
		host, port, e := net.SplitHostPort(address)
		if e != nil {
			return nil, e
		}
		ips, e := net.DefaultResolver.LookupIPAddr(ctx, host)
		if e != nil {
			return nil, e
		}
		for _, ip := range ips {
			if !publicIP(ip.IP) {
				continue
			}
			return d.DialContext(ctx, network, net.JoinHostPort(ip.IP.String(), port))
		}
		return nil, errors.New("destination resolves to a local address")
	}
	return &http.Client{Transport: tr, Timeout: 15 * time.Second, CheckRedirect: func(req *http.Request, via []*http.Request) error {
		if len(via) >= 5 || !safeURL(req.Context(), req.URL) {
			return errors.New("unsafe redirect")
		}
		return nil
	}}
}

func steamSearch(ctx context.Context, q string) ([]storeItem, error) {
	raw := "https://store.steampowered.com/api/storesearch/?term=" + url.QueryEscape(q) + "&l=english&cc=US"
	b, _, e := get(ctx, raw, 2<<20)
	if e != nil {
		return nil, e
	}
	var res struct {
		Items []storeItem `json:"items"`
	}
	e = json.Unmarshal(b, &res)
	return res.Items, e
}

func isStoreAppCandidate(kind string) bool {
	return strings.EqualFold(kind, "app") || strings.EqualFold(kind, "game")
}

func steamSearchMany(ctx context.Context, queries []string) [][]storeItem {
	results := make([][]storeItem, len(queries))
	gate := make(chan struct{}, 3)
	var wg sync.WaitGroup
	for i, query := range queries {
		wg.Add(1)
		go func(index int, q string) {
			defer wg.Done()
			select {
			case gate <- struct{}{}:
			case <-ctx.Done():
				return
			}
			defer func() { <-gate }()
			found, err := steamSearch(ctx, q)
			if err == nil {
				results[index] = found
			}
		}(i, query)
	}
	wg.Wait()
	return results
}

func steamPageSearchMany(ctx context.Context, queries []string) [][]storeItem {
	results := make([][]storeItem, len(queries))
	gate := make(chan struct{}, 3)
	var wg sync.WaitGroup
	for i, query := range queries {
		wg.Add(1)
		go func(index int, q string) {
			defer wg.Done()
			select {
			case gate <- struct{}{}:
			case <-ctx.Done():
				return
			}
			defer func() { <-gate }()
			raw := "https://store.steampowered.com/search/?term=" + url.QueryEscape(q) + "&l=english&cc=US"
			body, _, err := get(ctx, raw, 5<<20)
			if err != nil {
				return
			}
			for _, tag := range anchorRE.FindAllString(string(body), -1) {
				openEnd := strings.IndexByte(tag, '>')
				if openEnd < 0 {
					continue
				}
				attrs := attrs(tag[:openEnd+1])
				if !strings.Contains(attrs["class"], "search_result_row") {
					continue
				}
				idText := strings.Split(attrs["data-ds-appid"], ",")[0]
				id, _ := strconv.Atoi(idText)
				if id == 0 {
					if match := appRE.FindStringSubmatch(attrs["href"]); len(match) > 1 {
						id, _ = strconv.Atoi(match[1])
					}
				}
				if id <= 0 {
					continue
				}
				label := stripTags(tag[openEnd+1:])
				label = strings.TrimSpace(htmlUnescape(label))
				if label == "" {
					continue
				}
				results[index] = append(results[index], storeItem{ID: id, Name: label, Type: "game"})
			}
		}(i, query)
	}
	wg.Wait()
	return results
}

func steamArtwork(ctx context.Context, item storeItem) (*artwork, error) {
	u := fmt.Sprintf("https://store.steampowered.com/api/appdetails?appids=%d&l=english&cc=US", item.ID)
	b, _, e := get(ctx, u, 4<<20)
	if e != nil {
		return nil, e
	}
	var root map[string]appDetails
	if json.Unmarshal(b, &root) != nil {
		return nil, errors.New("invalid Steam response")
	}
	detail, ok := root[strconv.Itoa(item.ID)]
	if !ok || !detail.Success || !strings.EqualFold(detail.Data.Type, "game") {
		return nil, errors.New("Steam result is not a game")
	}
	// Prefer the dedicated portrait artwork for cards. The header is a landscape
	// banner and can contain padded/transparent mockups that look like a broken cover.
	cover := ""
	for _, base := range []string{"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/", "https://cdn.akamai.steamstatic.com/steam/apps/"} {
		cover = downloadImage(ctx, base+strconv.Itoa(item.ID)+"/library_600x900.jpg", fmt.Sprintf("%d_library_cover_v8.jpg", item.ID), coverValid)
		if cover != "" {
			break
		}
	}
	if cover == "" {
		cover = downloadImage(ctx, detail.Data.HeaderImage, fmt.Sprintf("%d_cover_v8.jpg", item.ID), coverValid)
	}
	banner := ""
	for _, name := range []string{"library_hero.jpg"} {
		for _, host := range []string{"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/", "https://cdn.akamai.steamstatic.com/steam/apps/"} {
			p := downloadImage(ctx, host+strconv.Itoa(item.ID)+"/"+name, fmt.Sprintf("%d_hero_v8.jpg", item.ID), bannerValid)
			if p != "" {
				banner = p
				break
			}
		}
		if banner != "" {
			break
		}
	}
	if banner == "" {
		rawURL := detail.Data.BackgroundRaw
		if rawURL == "" {
			rawURL = detail.Data.Background
		}
		banner = downloadImage(ctx, rawURL, fmt.Sprintf("%d_bg_v8.jpg", item.ID), bannerValid)
	}
	if cover == "" && item.TinyImage != "" {
		cover = downloadImage(ctx, item.TinyImage, fmt.Sprintf("%d_tiny_v8.jpg", item.ID), coverValid)
	}
	if cover == "" && banner != "" {
		cover = banner
	}
	if cover == "" && banner == "" {
		return nil, nil
	}
	return &artwork{cover, banner, detail.Data.ReleaseDate.Date}, nil
}
func downloadImage(ctx context.Context, raw, name string, valid func(int, int) bool) string {
	u, e := url.Parse(raw)
	if e != nil {
		return ""
	}
	b, h, e := get(ctx, u.String(), 20<<20)
	if e != nil {
		return ""
	}
	if mt := h.Get("Content-Type"); mt != "" && !strings.HasPrefix(strings.ToLower(mt), "image/") {
		return ""
	}
	cfg, format, e := image.DecodeConfig(bytes.NewReader(b))
	if e != nil || !(format == "jpeg" || format == "png" || format == "gif") || !valid(cfg.Width, cfg.Height) {
		return ""
	}
	path := filepath.Join(activeCache(ctx), name)
	if f, e := os.Open(path); e == nil {
		old, _, de := image.DecodeConfig(f)
		f.Close()
		if de == nil && valid(old.Width, old.Height) {
			return path
		}
	}
	return writeImageForContext(ctx, name, b)
}

// cacheRoot is attached to the request context by the protocol entry point.
type cacheKeyType struct{}

func activeCache(ctx context.Context) string { v, _ := ctx.Value(cacheKeyType{}).(string); return v }
func writeImageForContext(ctx context.Context, name string, b []byte) string {
	root := activeCache(ctx)
	if root == "" {
		return ""
	}
	path := filepath.Join(root, name)
	if fi, e := os.Stat(path); e == nil && fi.Size() > 0 {
		return path
	}
	tmp := path + ".tmp"
	if os.WriteFile(tmp, b, 0644) != nil {
		return ""
	}
	if os.Rename(tmp, path) != nil {
		_ = os.Remove(tmp)
		if fi, e := os.Stat(path); e == nil && fi.Size() > 0 {
			return path
		}
		return ""
	}
	return path
}
func coverValid(w, h int) bool {
	return w >= 200 && h >= 120 && float64(w)/float64(h) >= .45 && float64(w)/float64(h) <= 2.4
}
func bannerValid(w, h int) bool { return w >= 1000 && h >= 300 && float64(w)/float64(h) >= 2.7 }
func validCached(a artwork) bool {
	return fileValid(a.CoverPath, coverValid) || fileValid(a.CoverPath, bannerValid)
}
func fileValid(p string, valid func(int, int) bool) bool {
	if p == "" {
		return false
	}
	f, e := os.Open(p)
	if e != nil {
		return false
	}
	defer f.Close()
	cfg, _, e := image.DecodeConfig(f)
	return e == nil && valid(cfg.Width, cfg.Height)
}
func save(path string, a artwork) {
	b, e := json.Marshal(a)
	if e == nil {
		_ = os.WriteFile(path, b, 0644)
	}
	_ = os.Remove(path + ".miss")
}

func attrs(tag string) map[string]string {
	m := map[string]string{}
	for _, x := range attrRE.FindAllStringSubmatch(tag, -1) {
		value := x[2]
		if value == "" {
			value = x[3]
		}
		m[strings.ToLower(x[1])] = htmlUnescape(value)
	}
	return m
}
func htmlUnescape(s string) string {
	r := strings.NewReplacer("&amp;", "&", "&#39;", "'", "&quot;", `"`, "&lt;", "<", "&gt;", ">")
	return r.Replace(s)
}
func stripTags(s string) string {
	return strings.TrimSpace(regexp.MustCompile(`(?is)<[^>]+>`).ReplaceAllString(s, " "))
}
func pageImages(html string) []struct{ url, label string } {
	out := []struct{ url, label string }{}
	for _, tag := range tagRE.FindAllString(html, -1) {
		a := attrs(tag)
		u := a["content"]
		if u == "" {
			u = a["data-src"]
		}
		if u == "" {
			u = a["data-original"]
		}
		if u == "" {
			u = a["src"]
		}
		if u != "" {
			out = append(out, struct{ url, label string }{u, a["alt"] + " " + a["title"] + " " + a["class"]})
		}
	}
	return out
}
func releaseArtwork(ctx context.Context, title, raw, key string) *artwork {
	u, e := url.Parse(raw)
	if e != nil || !strings.Contains(strings.ToLower(u.Hostname()), "rutor") {
		return nil
	}
	b, _, e := get(ctx, u.String(), 4<<20)
	if e != nil {
		return nil
	}
	if titleMatch := titleRE.FindStringSubmatch(string(b)); len(titleMatch) > 1 {
		pageTitle := htmlUnescape(stripTags(titleMatch[1]))
		if !art.SameInstallments(title, pageTitle) || art.MatchScore(title, pageTitle) < .55 {
			return nil
		}
	} else if !art.SameInstallments(title, "") {
		return nil
	}
	imgs := pageImages(string(b))
	scores := []struct {
		p string
		s int64
	}{}
	for i, c := range imgs {
		if badLabel(c.url + " " + c.label) {
			continue
		}
		imgURL := c.url
		if rel, e := url.Parse(imgURL); e == nil && !rel.IsAbs() {
			imgURL = u.ResolveReference(rel).String()
		}
		p := downloadImage(ctx, imgURL, fmt.Sprintf("release_%s_%d_v8.jpg", key, i), coverValid)
		if p == "" {
			continue
		}
		fi, e := os.Stat(p)
		if e == nil {
			scores = append(scores, struct {
				p string
				s int64
			}{p, fi.Size()})
		}
	}
	if len(scores) == 0 {
		return nil
	}
	sort.Slice(scores, func(i, j int) bool { return scores[i].s > scores[j].s })
	return &artwork{CoverPath: scores[0].p}
}
func badLabel(s string) bool {
	return regexp.MustCompile(`(?i)(logo|avatar|smiley|emoji|flag|icon|banner-ad|pixel|spacer|magnet|torrent|placeholder|no[-_]?image|no[-_]?cover|blank|deals?|discounts?|price[-_ ]?alert|coupon)`).MatchString(s)
}

func trustedBingArtworkHost(host string) bool {
	host = strings.TrimSuffix(strings.ToLower(host), ".")
	for _, suffix := range []string{
		"static.wikia.nocookie.net", "fandom.com", "wikimedia.org", "gamefaqs.gamespot.com",
		"gamespot.com", "metacritic.com", "altarofgaming.com", "mobygames.com",
		"giantbomb.com", "igdb.com", "pcgamingwiki.com", "rawg.io", "lutris.net",
		"cdn.forza.net", "s-microsoft.com", "xbox.com", "steamstatic.com",
	} {
		if host == suffix || strings.HasSuffix(host, "."+suffix) {
			return true
		}
	}
	return false
}
func bingArtwork(ctx context.Context, title string, queries []string, key string) *artwork {
	for _, q := range queries[:min(2, len(queries))] {
		u := "https://www.bing.com/images/search?q=" + url.QueryEscape(q+" video game cover artwork") + "&qft=+filterui:imagesize-large"
		b, _, e := get(ctx, u, 5<<20)
		if e != nil {
			continue
		}
		for i, m := range regexp.MustCompile(`(?is)\bm\s*=\s*"([^"]+)"`).FindAllStringSubmatch(string(b), -1) {
			raw := htmlUnescape(m[1])
			var d map[string]string
			if json.Unmarshal([]byte(raw), &d) != nil {
				continue
			}
			imageURL := d["murl"]
			parsedImageURL, parseErr := url.Parse(imageURL)
			if imageURL == "" || parseErr != nil || !trustedBingArtworkHost(parsedImageURL.Hostname()) ||
				badLabel(imageURL+" "+d["t"]) || !art.SameInstallments(title, d["t"]) || art.MatchScore(q, d["t"]) < .66 {
				continue
			}
			p := downloadImage(ctx, imageURL, fmt.Sprintf("bing_%s_%d_v8.jpg", key, i), func(w, h int) bool {
				return w >= 250 && h >= 250 && float64(w)/float64(h) >= .45 && float64(w)/float64(h) <= 2.4
			})
			if p != "" {
				return &artwork{CoverPath: p}
			}
		}
	}
	return nil
}
func lutrisArtwork(ctx context.Context, title string, queries []string, key string) *artwork {
	for _, q := range queries[:min(3, len(queries))] {
		b, _, e := get(ctx, "https://lutris.net/games/?q="+url.QueryEscape(q), 4<<20)
		if e != nil {
			continue
		}
		for _, tag := range tagRE.FindAllString(string(b), -1) {
			a := attrs(tag)
			h := a["href"]
			m := regexp.MustCompile(`^/games/([^/?#]+)/?$`).FindStringSubmatch(h)
			if len(m) == 0 {
				continue
			}
			candidateTitle := stripTags(tag)
			if candidateTitle == "" {
				candidateTitle = strings.ReplaceAll(m[1], "-", " ")
			}
			if !art.SameInstallments(title, candidateTitle) || art.MatchScore(q, candidateTitle) < .45 {
				continue
			}
			p := downloadImage(ctx, "https://lutris.net/games/cover/"+m[1]+".jpg", "lutris_"+key+"_v8.jpg", coverValid)
			if p != "" {
				return &artwork{CoverPath: p}
			}
		}
	}
	return nil
}
func wikiArtwork(ctx context.Context, title string, queries []string, key string) *artwork {
	for _, q := range queries[:min(3, len(queries))] {
		u := "https://en.wikipedia.org/w/rest.php/v1/search/page?q=" + url.QueryEscape(q) + "&limit=5"
		b, _, e := get(ctx, u, 2<<20)
		if e != nil {
			continue
		}
		var res struct {
			Pages []page `json:"pages"`
		}
		if json.Unmarshal(b, &res) != nil {
			continue
		}
		for i, p := range res.Pages {
			if p.Thumbnail.URL == "" || !art.SameInstallments(title, p.Title) || art.MatchScore(q, p.Title) < .55 {
				continue
			}
			img := regexp.MustCompile(`width=\d+`).ReplaceAllString(p.Thumbnail.URL, "width=800")
			if img == p.Thumbnail.URL {
				img = regexp.MustCompile(`(?i)/\d+px-`).ReplaceAllString(img, "/800px-")
			}
			path := downloadImage(ctx, img, fmt.Sprintf("wiki_%s_%d_v8.jpg", key, i), coverValid)
			if path != "" {
				return &artwork{CoverPath: path}
			}
		}
	}
	return nil
}
