package xhttpnative

import (
	"encoding/json"
	"testing"
)

func testProfile(localPort, remotePort int, pin string) []byte {
	value := map[string]any{
		"log":      map[string]any{"loglevel": "warning"},
		"inbounds": []any{map[string]any{"tag": "wg-udp", "listen": "127.0.0.1", "port": localPort, "protocol": "tunnel", "settings": map[string]any{"allowedNetwork": "udp", "rewriteAddress": "127.0.0.1", "rewritePort": 51820}}},
		"outbounds": []any{map[string]any{"tag": "xhttp-get", "protocol": "vless", "settings": map[string]any{"address": "127.0.0.1", "port": remotePort, "id": "40168f45-37b1-4bfa-903c-9904f48489ae", "encryption": "none"}, "streamSettings": map[string]any{
			"network": "xhttp", "security": "tls", "tlsSettings": map[string]any{"serverName": "example.com", "alpn": []string{"h2"}, "fingerprint": "unsafe", "pinnedPeerCertSha256": pin},
			"xhttpSettings": map[string]any{"host": "example.com", "path": "/native/", "mode": "packet-up", "extra": map[string]any{"uplinkHTTPMethod": "GET", "uplinkDataPlacement": "header", "uplinkDataKey": "X-Data", "uplinkChunkSize": 2048, "scMaxEachPostBytes": 2048, "scMinPostsIntervalMs": 1, "xPaddingBytes": "100-100"}},
		}}},
	}
	data, _ := json.Marshal(value)
	return data
}

func TestProfileRejectsOtherTransports(t *testing.T) {
	data := testProfile(41000, 443, "")
	if _, err := validateProfile(data); err != nil {
		t.Fatal(err)
	}
	var value map[string]any
	json.Unmarshal(data, &value)
	value["routing"] = map[string]any{}
	changed, _ := json.Marshal(value)
	if _, err := validateProfile(changed); err == nil {
		t.Fatal("accepted extra routing")
	}
	if _, err := validateProfile(append(data, []byte(` {}`)...)); err == nil {
		t.Fatal("accepted trailing JSON")
	}
}
