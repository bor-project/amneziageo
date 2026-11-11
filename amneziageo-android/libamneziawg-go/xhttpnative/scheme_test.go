package xhttpnative

import (
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"
)

func plainTestProfile(localPort, remotePort int) []byte {
	var value profile
	json.Unmarshal(testProfile(localPort, remotePort, ""), &value)
	stream := &value.Outbounds[0].Stream
	stream.Security = "none"
	stream.TLS.Name = ""
	stream.TLS.ALPN = nil
	stream.TLS.Fingerprint = ""
	data, _ := json.Marshal(value)
	return data
}

func TestPlainHTTPProfilesAreRejectedBeforeDial(t *testing.T) {
	var requests atomic.Int32
	front := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { requests.Add(1) }))
	defer front.Close()
	for _, scheme := range []string{"", "http", "none", "automatic"} {
		var value profile
		json.Unmarshal(plainTestProfile(freeUDP(t), front.Listener.Addr().(*net.TCPAddr).Port), &value)
		value.Outbounds[0].Stream.Security = scheme
		data, _ := json.Marshal(value)
		if _, err := Start(data); !errors.Is(err, ErrProfile) {
			t.Fatalf("accepted plaintext security %q: %v", scheme, err)
		}
	}
	if requests.Load() != 0 {
		t.Fatal("plaintext request escaped")
	}
}

func TestTLSProxyDoesNotForwardCredentialsToRedirect(t *testing.T) {
	var forwarded atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { forwarded.Add(1) }))
	defer target.Close()
	front := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, target.URL, http.StatusTemporaryRedirect)
	}))
	defer front.Close()
	host, _ := startTestHost(t, front)
	done := make(chan error, 1)
	go func() { done <- host.Wait() }()
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("redirect accepted as proxy")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("redirect was retried")
	}
	if forwarded.Load() != 0 {
		t.Fatal("credential followed redirect")
	}
}
