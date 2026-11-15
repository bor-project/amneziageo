package xhttpnative

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"sync"
	"testing"
	"time"
)

func TestManualAuthorizationCoversEverySignedOperation(t *testing.T) {
	var mu sync.Mutex
	seen := map[string]bool{}
	handler := signedTestHandler(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-WG-Version", "2")
		w.Header().Set("X-WG-Seq", "0")
		if r.URL.Query().Get("op") == "pull" {
			w.WriteHeader(http.StatusNoContent)
		}
	}))
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer "+authFixture {
			t.Error("missing explicit bearer")
			w.WriteHeader(401)
			return
		}
		if r.URL.Query().Get("token") != "" || r.URL.Query().Get("credential") != "" {
			t.Error("credential entered the URL")
		}
		mu.Lock()
		seen[r.URL.Query().Get("op")] = true
		mu.Unlock()
		clean := r.Clone(r.Context())
		clean.Header = r.Header.Clone()
		clean.Header.Del("Authorization")
		handler.ServeHTTP(w, clean)
	}))
	defer server.Close()
	digest := sha256.Sum256(server.Certificate().Raw)
	data := testProfile(freeUDP(t), server.Listener.Addr().(*net.TCPAddr).Port, hex.EncodeToString(digest[:]))
	var profile map[string]any
	if err := json.Unmarshal(data, &profile); err != nil {
		t.Fatal(err)
	}
	outbound := profile["outbounds"].([]any)[0].(map[string]any)
	outbound["streamSettings"].(map[string]any)["xhttpSettings"].(map[string]any)["extra"].(map[string]any)["authorizationHeader"] = true
	data, _ = json.Marshal(profile)
	h, err := Start(data)
	if err != nil {
		t.Fatal(err)
	}
	defer stopHost(t, h)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := h.WaitReady(ctx); err != nil {
		t.Fatal(err)
	}
	for _, operation := range []string{"push", "pull", "close"} {
		if _, _, _, err := h.signedRequest(ctx, operation, url.Values{}, nil); err != nil {
			t.Fatal(err)
		}
	}
	mu.Lock()
	defer mu.Unlock()
	for _, operation := range []string{"challenge", "open", "push", "pull", "close"} {
		if !seen[operation] {
			t.Errorf("operation %s was not authorized", operation)
		}
	}
}
