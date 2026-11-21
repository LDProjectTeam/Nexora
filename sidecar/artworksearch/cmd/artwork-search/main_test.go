package main

import "testing"

func TestTrustedBingArtworkHost(t *testing.T) {
	for _, tc := range []struct {
		host  string
		trust bool
	}{
		{"static.wikia.nocookie.net", true},
		{"images.metacritic.com", true},
		{"gamefaqs.gamespot.com", true},
		{"xbdeals.net", false},
		{"random-cdn.example", false},
	} {
		if got := trustedBingArtworkHost(tc.host); got != tc.trust {
			t.Errorf("trustedBingArtworkHost(%q)=%v, want %v", tc.host, got, tc.trust)
		}
	}
}
