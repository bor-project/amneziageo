package xhttpnative

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net"
	"regexp"
	"strings"
)

var ErrProfile = errors.New("xhttp-bad-profile")
var uuid = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)

type profile struct {
	Log struct {
		LogLevel string `json:"loglevel"`
	} `json:"log"`
	Inbounds []struct {
		Tag      string `json:"tag"`
		Listen   string `json:"listen"`
		Port     int    `json:"port"`
		Protocol string `json:"protocol"`
		Settings struct {
			Network string `json:"allowedNetwork"`
			Address string `json:"rewriteAddress"`
			Port    int    `json:"rewritePort"`
		} `json:"settings"`
	} `json:"inbounds"`
	Outbounds []struct {
		Tag      string `json:"tag"`
		Protocol string `json:"protocol"`
		Settings struct {
			Address    string `json:"address"`
			Port       int    `json:"port"`
			ID         string `json:"id"`
			Encryption string `json:"encryption"`
		} `json:"settings"`
		Stream struct {
			Network  string `json:"network"`
			Security string `json:"security"`
			TLS      struct {
				Name        string   `json:"serverName"`
				ALPN        []string `json:"alpn"`
				Fingerprint string   `json:"fingerprint"`
				Pin         string   `json:"pinnedPeerCertSha256,omitempty"`
			} `json:"tlsSettings"`
			XHTTP struct {
				Host  string `json:"host"`
				Path  string `json:"path"`
				Mode  string `json:"mode"`
				Extra struct {
					Method    string `json:"uplinkHTTPMethod"`
					Placement string `json:"uplinkDataPlacement"`
					Key       string `json:"uplinkDataKey"`
					Chunk     int    `json:"uplinkChunkSize"`
					Max       int    `json:"scMaxEachPostBytes"`
					Interval  int    `json:"scMinPostsIntervalMs"`
					Padding   string `json:"xPaddingBytes"`
				} `json:"extra"`
			} `json:"xhttpSettings"`
		} `json:"streamSettings"`
	} `json:"outbounds"`
}

func validateProfile(data []byte) ([]byte, error) {
	if len(data) == 0 || len(data) > 32768 {
		return nil, ErrProfile
	}
	var value profile
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&value); err != nil {
		return nil, ErrProfile
	}
	if decoder.Decode(new(any)) != ioEOF {
		return nil, ErrProfile
	}
	if len(value.Inbounds) != 1 || len(value.Outbounds) != 1 {
		return nil, ErrProfile
	}
	inbound, outbound := value.Inbounds[0], value.Outbounds[0]
	if inbound.Listen != "127.0.0.1" || !validPort(inbound.Port) || inbound.Protocol != "tunnel" || inbound.Settings.Network != "udp" || !validHost(inbound.Settings.Address) || !validPort(inbound.Settings.Port) {
		return nil, ErrProfile
	}
	stream := outbound.Stream
	if outbound.Protocol != "vless" || net.ParseIP(outbound.Settings.Address) == nil || !validPort(outbound.Settings.Port) || !uuid.MatchString(outbound.Settings.ID) || outbound.Settings.Encryption != "none" || stream.Network != "xhttp" || (stream.Security != "tls" && stream.Security != "none") {
		return nil, ErrProfile
	}
	tls := stream.TLS
	if stream.Security == "none" && (tls.Name != "" || len(tls.ALPN) != 0 || tls.Fingerprint != "" || tls.Pin != "") {
		return nil, ErrProfile
	}
	if stream.Security == "tls" && (!validHost(tls.Name) || len(tls.ALPN) != 1 || tls.ALPN[0] != "h2" || tls.Fingerprint != "unsafe") {
		return nil, ErrProfile
	}
	if tls.Pin != "" {
		decoded, err := hex.DecodeString(tls.Pin)
		if err != nil || len(decoded) != 32 {
			return nil, ErrProfile
		}
	}
	xhttp := stream.XHTTP
	if !validHost(xhttp.Host) || !strings.HasPrefix(xhttp.Path, "/") || !strings.HasSuffix(xhttp.Path, "/") || len(xhttp.Path) < 3 || strings.ContainsAny(xhttp.Path, "\r\n?#") || xhttp.Mode != "packet-up" {
		return nil, ErrProfile
	}
	extra := xhttp.Extra
	if extra.Method != "GET" || extra.Placement != "header" || extra.Key != "X-Data" || extra.Chunk < 1 || extra.Chunk > 2048 || extra.Max < 1 || extra.Max > 2048 || extra.Interval < 1 || extra.Interval > 1000 || extra.Padding != "100-100" {
		return nil, ErrProfile
	}
	value.Log.LogLevel = "none"
	output, err := json.Marshal(value)
	if err != nil {
		return nil, ErrProfile
	}
	return output, nil
}

func validPort(value int) bool { return value > 0 && value <= 65535 }
func validHost(value string) bool {
	return value != "" && len(value) <= 253 && !strings.ContainsAny(value, "\r\n\t /\\@?#") && (net.ParseIP(value) != nil || !strings.Contains(value, ":"))
}
