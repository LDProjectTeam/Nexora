package artworksearch

import "testing"

func TestNormalizeTitle(t *testing.T) {
	if got := Normalize("Diablo II: Resurrected - Ultimate Edition"); got != "diablo ii resurrected ultimate edition" {
		t.Fatalf("Normalize returned %q", got)
	}
}

func TestMatchScoreInstallments(t *testing.T) {
	cases := []struct {
		q, c string
		want bool
	}{
		{"Call of Duty Black Ops 1", "Call of Duty Black Ops", true},
		{"Call of Duty Black Ops 1", "Call of Duty Black Ops II", false},
		{"Call of Duty Black Ops 1", "Call of Duty Black Ops III", false},
		{"Call of Duty Black Ops 2", "Call of Duty Black Ops II", true},
		{"Call of Duty Black Ops 1", "Call of Duty Black Ops Cold War", false},
		{"Call of Duty Black Ops", "Call of Duty Black Ops Cold War", false},
		{"Call of Duty Black Ops Collection Edition", "Call of Duty Black Ops Cold War", false},
		{"Call of Duty Black Ops Cold War", "Call of Duty Black Ops", false},
		{"Call of Duty Black Ops 3 Zombies Chronicles", "Call of Duty Black Ops 3 Digital Deluxe", false},
		{"Call of Duty Black Ops 3 Zombies Chronicles", "Call of Duty Black Ops 3 Zombies Chronicles Deluxe Edition", true},
		{"Spider-Man: Web of Shadows", "Marvel's Spider-Man Remastered", false},
		{"Ultimate Spider-Man", "Marvel's Spider-Man Remastered", false},
		{"Spider-Man: Shattered Dimensions", "Spider-Man: Edge of Time", false},
		{"Marvel's Spider-Man Remastered", "Marvel's Spider-Man Remastered", true},
		{"Marvel's Spider-Man Miles Morales", "Marvel's Spider-Man Remastered", false},
		{"The Amazing Spider-Man 2", "Spider-Man 2: The Game", false},
		{"Prince of Persia The Two Thrones", "Prince of Persia Warrior Within", false},
		{"Tomb Raider Legend", "Tomb Raider Underworld", false},
		{"Doom Eternal", "Doom 2016", false},
		{"The Witcher 3", "The Witcher III Wild Hunt", true},
		{"Cyberpunk 2077 Ultimate Edition [v 2.5] PC RePack", "Cyberpunk 2077", true},
		{"Call of Duty Black Ops 2 [v 1.05] PC RePack", "Call of Duty Black Ops II", true},
		{"Call of Duty Black Ops 2 [v 1.05] PC RePack", "Call of Duty Black Ops III", false},
		{"Diablo II Resurrected", "Diablo IV", false},
		{"The Witcher 3 Wild Hunt", "The Witcher III Wild Hunt Complete Edition", true},
		{"Forza Horizon 2 Ultimate Edition", "Forza Horizon 2 Cover", true},
		{"Metro Redux Dilogy", "Metro 2033 Redux", true},
		{"Metro Redux Dilogy", "Metro Last Light Redux", true},
	}
	for _, tc := range cases {
		if got := MatchScore(tc.q, tc.c) >= .66; got != tc.want {
			t.Errorf("MatchScore(%q,%q)=%v", tc.q, tc.c, got)
		}
	}
}

func TestSearchQueriesStripsReleaseNoise(t *testing.T) {
	q := SearchQueries("ContractVille & All DLCs Bundle [v 1.9.2] PC | repack")
	if len(q) == 0 || q[0] != "ContractVille & All DLCs Bundle" {
		t.Fatalf("unexpected queries: %#v", q)
	}
}

func TestSearchQueriesKeepsFourDigitNumbersInGameTitles(t *testing.T) {
	queries := SearchQueries("Cohen 1939 (2026) PC | RePack от FitGirl")
	if len(queries) == 0 || queries[0] != "Cohen 1939" {
		t.Fatalf("expected title year to remain in query, got %#v", queries)
	}
	if MatchScore("Cohen 1939", "Cohen 1939") < .66 {
		t.Fatal("exact game title with a four-digit number did not match")
	}
}

func TestProgressiveQueriesTrimOneTrailingWordAtATime(t *testing.T) {
	queries := ProgressiveSearchQueries("Forza Horizon 2 Ultimate Edition [v1.4] (2014) PC | RePack")
	want := []string{"Forza Horizon 2 Ultimate", "Forza Horizon"}
	if len(queries) < len(want) {
		t.Fatalf("expected progressive fallbacks, got %#v", queries)
	}
	for i := range want {
		if queries[i] != want[i] {
			t.Fatalf("fallback %d = %q, want %q (all: %#v)", i, queries[i], want[i], queries)
		}
	}
}
