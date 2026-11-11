package transportnative

import (
	"context"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/httpgetauth"
	"github.com/bor-project/amneziageo/libamneziawg-go/internal/proxytls"
	"github.com/bor-project/amneziageo/libamneziawg-go/xhttpnative"
	"github.com/quic-go/quic-go"
)

func TestTypedPermanentFailuresKeepTheirOuterClassification(t *testing.T) {
	for _, item := range []struct {
		err  error
		code string
	}{
		{fmt.Errorf("response: %w", httpgetauth.ErrFields), "proxy-bad-answer"},
		{fmt.Errorf("TLS: %w", tls.RecordHeaderError{Msg: "first record"}), "proxy-tls"},
		{fmt.Errorf("HTTPS: %w", http.ErrSchemeMismatch), "proxy-tls"},
		{errors.New("http-get-challenge-status-503"), "proxy-no-answer"},
	} {
		if code := FaultCode(item.err); code != item.code {
			t.Fatalf("FaultCode(%T) = %s, want %s", item.err, code, item.code)
		}
	}
}

func fixture(method string, certificate bool) map[string]any {
	return map[string]any{"version": 2, "transport": "http", "listen": map[string]any{"host": "127.0.0.1", "port": 57331},
		"target": map[string]any{"host": "udp.example", "port": 51820},
		"front": map[string]any{"host": "proxy.example", "port": 80, "path": "transport", "dialAddress": "127.0.0.1",
			"tls": map[string]any{"enabled": certificate, "serverName": "proxy.example", "pin": ""}},
		"auth": map[string]any{"scheme": "hmac-sha256-v2", "credential": "22222222-2222-2222-2222-222222222222"},
		"http": map[string]any{"version": 2, "method": method, "dataPlacement": map[string]string{"GET": "header", "POST": "body"}[method]}}
}

func encoded(t *testing.T, value any) []byte {
	t.Helper()
	data, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestExplicitMethodsAndCertificates(t *testing.T) {
	for _, method := range []string{"GET", "POST"} {
		for _, certificate := range []bool{false, true} {
			p, err := Parse(encoded(t, fixture(method, certificate)), false)
			if !certificate {
				if !errors.Is(err, ErrProfile) {
					t.Fatal("plaintext profile accepted", err)
				}
				continue
			}
			if err != nil {
				t.Fatal(method, certificate, err)
			}
			legacy, err := p.HTTPProfile()
			if err != nil {
				t.Fatal(err)
			}
			var saved map[string]any
			json.Unmarshal(legacy, &saved)
			stream := saved["outbounds"].([]any)[0].(map[string]any)["streamSettings"].(map[string]any)
			extra := stream["xhttpSettings"].(map[string]any)["extra"].(map[string]any)
			if extra["uplinkHTTPMethod"] != method || (stream["security"] == "tls") != certificate {
				t.Fatal(stream)
			}
		}
	}
}

func TestInvalidModesNeverSelectAnotherCarrier(t *testing.T) {
	cases := map[string]func(map[string]any){
		"version":                  func(v map[string]any) { v["version"] = 3 },
		"transport":                func(v map[string]any) { v["transport"] = "websocket" },
		"method":                   func(v map[string]any) { v["http"].(map[string]any)["method"] = "PUT" },
		"get-body":                 func(v map[string]any) { v["http"].(map[string]any)["dataPlacement"] = "body" },
		"missing-certificate-flag": func(v map[string]any) { delete(v["front"].(map[string]any)["tls"].(map[string]any), "enabled") },
		"bad-pin": func(v map[string]any) {
			v["front"].(map[string]any)["tls"].(map[string]any)["pin"] = strings.Repeat("z", 64)
		},
		"dial-dns":        func(v map[string]any) { v["front"].(map[string]any)["dialAddress"] = "proxy.example" },
		"public-listener": func(v map[string]any) { v["listen"].(map[string]any)["host"] = "0.0.0.0" },
		"unknown-field":   func(v map[string]any) { v["unexpected"] = true },
		"target":          func(v map[string]any) { v["target"].(map[string]any)["port"] = 0 },
	}
	for name, change := range cases {
		t.Run(name, func(t *testing.T) {
			v := fixture("GET", true)
			change(v)
			if _, err := Start(encoded(t, v), nil); !errors.Is(err, ErrProfile) {
				t.Fatal(err)
			}
		})
	}
}

func TestQuicRequiresItsCertificateAndVersion(t *testing.T) {
	v := fixture("GET", true)
	v["transport"] = "quic"
	delete(v, "http")
	v["auth"] = map[string]any{"scheme": "bearer", "credential": "fixture"}
	v["quic"] = map[string]any{"version": 1, "protocol": "webtransport", "datagrams": "framed-stream"}
	if _, err := Parse(encoded(t, v), false); err != nil {
		t.Fatal(err)
	}
	v["front"].(map[string]any)["tls"].(map[string]any)["enabled"] = false
	if _, err := Parse(encoded(t, v), false); !errors.Is(err, ErrProfile) {
		t.Fatal(err)
	}
}

func TestQuicMetadataPreservesTheDestination(t *testing.T) {
	frame, err := tunnelPreamble(Address{"udp.example", 51820})
	if err != nil {
		t.Fatal(err)
	}
	if int(binary.BigEndian.Uint16(frame[:2])) != len(frame)-2 {
		t.Fatal("preamble length")
	}
	parts := strings.Split(string(frame[2:]), ".")
	if len(parts) != 3 {
		t.Fatal("JWT framing")
	}
	data, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		t.Fatal(err)
	}
	var body struct {
		R  string
		Rp int
		P  map[string]any
	}
	if json.Unmarshal(data, &body) != nil || body.R != "udp.example" || body.Rp != 51820 || body.P["Udp"] == nil {
		t.Fatal(string(data))
	}
}

func TestAuthenticatedTargetRefusalRemainsFatal(t *testing.T) {
	err := streamFault(&quic.StreamError{ErrorCode: quic.StreamErrorCode(firstStreamError + 403 + 403/30)})
	if FaultCode(err) != "proxy-target-denied" {
		t.Fatal(err)
	}
	if FaultCode(errors.New("http-get-open-status-403")) != "proxy-target-denied" {
		t.Fatal("HTTP destination rejection")
	}
	if FaultCode(errors.New("http-get-auth-key-rejected")) != "proxy-auth" {
		t.Fatal("authorization rejection")
	}
}

func TestCancelledReadinessReleasesTheLocalSocket(t *testing.T) {
	xhttpnative.InstallProtector(func(int) bool { return true })
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	port := socket.LocalAddr().(*net.UDPAddr).Port
	socket.Close()
	v := fixture("POST", true)
	v["listen"].(map[string]any)["port"] = port
	host, err := Start(encoded(t, v), nil)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if !errors.Is(host.WaitReady(ctx), context.Canceled) {
		t.Fatal("readiness ignores cancellation")
	}
	stop, end := context.WithTimeout(context.Background(), time.Second)
	defer end()
	if err := host.Stop(stop); err != nil {
		t.Fatal(err)
	}
	opened, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port})
	if err != nil {
		t.Fatal(err)
	}
	opened.Close()
}

func TestRemoteConnectRefusalIsDistinctFromNetworkLoss(t *testing.T) {
	refused := connectFault(&quic.ApplicationError{Remote: true, ErrorCode: 0})
	if FaultCode(refused) != "proxy-rejected" {
		t.Fatal(refused)
	}
	network := errors.New("network failure")
	if connectFault(network) != network {
		t.Fatal("network failure reclassified")
	}
	local := &quic.ApplicationError{Remote: false, ErrorCode: 0}
	if connectFault(local) != local {
		t.Fatal("local cancellation reclassified")
	}
}

func TestPinnedTlsFailureIsReportedAsTls(t *testing.T) {
	if FaultCode(proxytls.ErrIdentity) != "proxy-tls" {
		t.Fatal("pinned identity failure changed reason")
	}
}
