package xhttpnative

import (
	"context"
	"encoding/json"
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

func TestPlainHTTPRequiresExplicitProfile(t *testing.T) {
	for _, scheme := range []string{"", "http", "automatic"} {
		var value profile
		json.Unmarshal(plainTestProfile(41000, 80), &value)
		value.Outbounds[0].Stream.Security = scheme
		data, _ := json.Marshal(value)
		if _, err := validateProfile(data); err == nil {
			t.Fatalf("accepted ambiguous security %q", scheme)
		}
	}
	if _, err := validateProfile(plainTestProfile(41000, 80)); err != nil {
		t.Fatal(err)
	}
	for _, field := range []string{"name", "alpn", "fingerprint", "pin"} {
		var value profile
		json.Unmarshal(plainTestProfile(41000, 80), &value)
		stream := &value.Outbounds[0].Stream
		switch field {
		case "name":
			stream.TLS.Name = "example.com"
		case "alpn":
			stream.TLS.ALPN = []string{"h2"}
		case "fingerprint":
			stream.TLS.Fingerprint = "unsafe"
		case "pin":
			stream.TLS.Pin = "0000000000000000000000000000000000000000000000000000000000000000"
		}
		data, _ := json.Marshal(value)
		if _, err := validateProfile(data); err == nil {
			t.Fatalf("accepted ignored TLS %s", field)
		}
	}
}

func TestPlainHTTPDatagramsRoundTripAndRebind(t *testing.T) {
	tlsServer, bad := getServer(t)
	defer tlsServer.Close()
	server := httptest.NewServer(tlsServer.Config.Handler)
	defer server.Close()
	port := freeUDP(t)
	host, err := Start(plainTestProfile(port, server.Listener.Addr().(*net.TCPAddr).Port))
	if err != nil {
		t.Fatal(err)
	}
	defer stopHost(t, host)
	roundTrip(t, port, []int{33, 148, 1280, 2048})
	roundTrip(t, port, []int{148, 1280})
	if bad.Load() != 0 || host.base.Scheme != "http" {
		t.Fatal("plain HTTP envelope or rebind violated")
	}
}

func TestPlainHTTPDoesNotForwardCredentialsToRedirect(t *testing.T) {
	var forwarded atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { forwarded.Add(1) }))
	defer target.Close()
	front := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, target.URL, http.StatusTemporaryRedirect)
	}))
	defer front.Close()
	host, err := Start(plainTestProfile(freeUDP(t), front.Listener.Addr().(*net.TCPAddr).Port))
	if err != nil {
		t.Fatal(err)
	}
	defer stopHost(t, host)
	done := make(chan error, 1)
	go func() { done <- host.Wait() }()
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("redirect accepted as a proxy")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("redirect was retried")
	}
	if forwarded.Load() != 0 {
		t.Fatal("credential followed a redirect")
	}
}

func TestPlainHTTPHonorsSocketProtection(t *testing.T) {
	rejectProtect.Store(true)
	defer rejectProtect.Store(false)
	var requests atomic.Int32
	front := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { requests.Add(1) }))
	defer front.Close()
	host, err := Start(plainTestProfile(freeUDP(t), front.Listener.Addr().(*net.TCPAddr).Port))
	if err != nil {
		t.Fatal(err)
	}
	defer stopHost(t, host)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	select {
	case <-host.done:
	case <-ctx.Done():
		t.Fatal("protection failure hung")
	}
	if requests.Load() != 0 || host.fault == nil {
		t.Fatal("unprotected HTTP request escaped")
	}
}
