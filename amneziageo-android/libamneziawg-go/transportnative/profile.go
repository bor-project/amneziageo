package transportnative

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"regexp"
	"strings"
)

var ErrProfile = errors.New("proxy-bad-profile")
var pathPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)
var credentialPattern = regexp.MustCompile(`^[A-Za-z0-9._~+/=-]{1,512}$`)
var uuidPattern = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)

type Address struct {
	Host string `json:"host"`
	Port int    `json:"port"`
}

type Profile struct {
	Version   int     `json:"version"`
	Transport string  `json:"transport"`
	Listen    Address `json:"listen"`
	Target    Address `json:"target"`
	Front     struct {
		Host        string `json:"host"`
		Port        int    `json:"port"`
		Path        string `json:"path"`
		DialAddress string `json:"dialAddress"`
		TLS         struct {
			Enabled    *bool  `json:"enabled"`
			ServerName string `json:"serverName"`
			Pin        string `json:"pin"`
		} `json:"tls"`
	} `json:"front"`
	Auth struct {
		Scheme     string `json:"scheme"`
		Credential string `json:"credential"`
	} `json:"auth"`
	HTTP *struct {
		Version       int    `json:"version"`
		Method        string `json:"method"`
		DataPlacement string `json:"dataPlacement"`
	} `json:"http,omitempty"`
	QUIC *struct {
		Version   int    `json:"version"`
		Protocol  string `json:"protocol"`
		Datagrams string `json:"datagrams"`
	} `json:"quic,omitempty"`
}

func Parse(data []byte, probe bool) (Profile, error) {
	var p Profile
	if len(data) == 0 || len(data) > 32768 {
		return p, ErrProfile
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&p) != nil || decoder.Decode(new(any)) != io.EOF || p.Version != 2 ||
		!validHost(p.Front.Host) || !validPort(p.Front.Port) || net.ParseIP(p.Front.DialAddress) == nil ||
		!pathPattern.MatchString(p.Front.Path) || p.Front.TLS.Enabled == nil || !*p.Front.TLS.Enabled {
		return p, ErrProfile
	}
	if probe {
		if p.Transport != "quic" || p.Listen != (Address{}) || p.Target != (Address{}) {
			return p, ErrProfile
		}
	} else if p.Listen.Host != "127.0.0.1" || !validPort(p.Listen.Port) || !validHost(p.Target.Host) || !validPort(p.Target.Port) {
		return p, ErrProfile
	}
	if *p.Front.TLS.Enabled {
		if !validHost(p.Front.TLS.ServerName) {
			return p, ErrProfile
		}
		if p.Front.TLS.Pin != "" {
			pin, err := hex.DecodeString(p.Front.TLS.Pin)
			if err != nil || len(pin) != 32 {
				return p, ErrProfile
			}
		}
	} else if p.Front.TLS.Pin != "" {
		return p, ErrProfile
	}
	switch p.Transport {
	case "http":
		if p.Auth.Scheme != "hmac-sha256-v2" || !uuidPattern.MatchString(p.Auth.Credential) ||
			strings.ReplaceAll(p.Auth.Credential, "-", "") == strings.Repeat("0", 32) || p.HTTP == nil || p.QUIC != nil ||
			p.HTTP.Version != 2 || !(p.HTTP.Method == "GET" && p.HTTP.DataPlacement == "header" || p.HTTP.Method == "POST" && p.HTTP.DataPlacement == "body") {
			return p, ErrProfile
		}
	case "quic":
		if !*p.Front.TLS.Enabled || p.Auth.Scheme != "bearer" || !credentialPattern.MatchString(p.Auth.Credential) ||
			p.HTTP != nil || p.QUIC == nil || p.QUIC.Version != 1 || p.QUIC.Protocol != "webtransport" || p.QUIC.Datagrams != "framed-stream" {
			return p, ErrProfile
		}
	default:
		return p, ErrProfile
	}
	return p, nil
}

func validPort(port int) bool { return port > 0 && port <= 65535 }
func validHost(host string) bool {
	return host != "" && len(host) <= 253 && !strings.ContainsAny(host, "\r\n\t /\\@?#") &&
		(net.ParseIP(host) != nil || !strings.Contains(host, ":"))
}

func (p Profile) HTTPProfile() ([]byte, error) {
	if p.Transport != "http" || p.HTTP == nil || p.Front.TLS.Enabled == nil || !*p.Front.TLS.Enabled {
		return nil, ErrProfile
	}
	security := "tls"
	tlsSettings := map[string]any{"serverName": p.Front.TLS.ServerName, "alpn": []string{"h2"}, "fingerprint": "unsafe"}
	if p.Front.TLS.Pin != "" {
		tlsSettings["pinnedPeerCertSha256"] = p.Front.TLS.Pin
	}
	return json.Marshal(map[string]any{
		"log": map[string]any{"loglevel": "none"},
		"inbounds": []any{map[string]any{"tag": "udp", "listen": p.Listen.Host, "port": p.Listen.Port, "protocol": "tunnel",
			"settings": map[string]any{"allowedNetwork": "udp", "rewriteAddress": p.Target.Host, "rewritePort": p.Target.Port}}},
		"outbounds": []any{map[string]any{"tag": "http", "protocol": "vless",
			"settings": map[string]any{"address": p.Front.DialAddress, "port": p.Front.Port, "id": p.Auth.Credential, "encryption": "none"},
			"streamSettings": map[string]any{"network": "xhttp", "security": security, "tlsSettings": tlsSettings,
				"xhttpSettings": map[string]any{"host": p.Front.Host, "path": "/" + p.Front.Path + "/", "mode": "packet-up",
					"extra": map[string]any{"uplinkHTTPMethod": p.HTTP.Method, "uplinkDataPlacement": p.HTTP.DataPlacement,
						"uplinkDataKey": "X-Data", "uplinkChunkSize": 2048, "scMaxEachPostBytes": 2048, "scMinPostsIntervalMs": 1, "xPaddingBytes": "100-100"}}}}},
	})
}
