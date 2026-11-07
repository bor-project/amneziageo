package xhttpnative

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net"
	"regexp"
	"strings"

	"github.com/xtls/xray-core/app/dispatcher"
	xlog "github.com/xtls/xray-core/app/log"
	"github.com/xtls/xray-core/app/proxyman"
	xnet "github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/common/protocol"
	"github.com/xtls/xray-core/common/serial"
	"github.com/xtls/xray-core/core"
	"github.com/xtls/xray-core/proxy/dokodemo"
	"github.com/xtls/xray-core/proxy/vless"
	vlessout "github.com/xtls/xray-core/proxy/vless/outbound"
	"github.com/xtls/xray-core/transport/internet"
	"github.com/xtls/xray-core/transport/internet/splithttp"
	"github.com/xtls/xray-core/transport/internet/tls"
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
	if outbound.Protocol != "vless" || net.ParseIP(outbound.Settings.Address) == nil || !validPort(outbound.Settings.Port) || !uuid.MatchString(outbound.Settings.ID) || outbound.Settings.Encryption != "none" || stream.Network != "xhttp" || stream.Security != "tls" {
		return nil, ErrProfile
	}
	tls := stream.TLS
	if !validHost(tls.Name) || len(tls.ALPN) != 1 || tls.ALPN[0] != "h2" || tls.Fingerprint != "unsafe" {
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

func buildConfig(data []byte) (*core.Config, error) {
	var value profile
	if err := json.Unmarshal(data, &value); err != nil {
		return nil, ErrProfile
	}
	inbound, outbound := value.Inbounds[0], value.Outbounds[0]
	stream := outbound.Stream
	tlsSettings := &tls.Config{ServerName: stream.TLS.Name, NextProtocol: []string{"h2"}, Fingerprint: "unsafe"}
	if stream.TLS.Pin != "" {
		pin, _ := hex.DecodeString(stream.TLS.Pin)
		tlsSettings.PinnedPeerCertSha256 = [][]byte{pin}
	}
	tlsMessage := serial.ToTypedMessage(tlsSettings)
	extra := stream.XHTTP.Extra
	interval := func(value int) *splithttp.RangeConfig {
		return &splithttp.RangeConfig{From: int32(value), To: int32(value)}
	}
	xhttp := &splithttp.Config{
		Host: stream.XHTTP.Host, Path: stream.XHTTP.Path, Mode: "packet-up",
		UplinkHTTPMethod: "GET", UplinkDataPlacement: "header", UplinkDataKey: "X-Data",
		UplinkChunkSize: interval(extra.Chunk), ScMaxEachPostBytes: interval(extra.Max),
		ScMinPostsIntervalMs: interval(extra.Interval), XPaddingBytes: interval(100),
		Xmux: &splithttp.XmuxConfig{MaxConnections: interval(3), HMaxRequestTimes: &splithttp.RangeConfig{From: 600, To: 900}, HMaxReusableSecs: &splithttp.RangeConfig{From: 1800, To: 3000}},
	}
	address := func(host string) *xnet.IPOrDomain { return xnet.NewIPOrDomain(xnet.ParseAddress(host)) }
	return &core.Config{
		App: []*serial.TypedMessage{
			serial.ToTypedMessage(&xlog.Config{ErrorLogType: xlog.LogType_None, AccessLogType: xlog.LogType_None}),
			serial.ToTypedMessage(&dispatcher.Config{}), serial.ToTypedMessage(&proxyman.OutboundConfig{}), serial.ToTypedMessage(&proxyman.InboundConfig{}),
		},
		Inbound: []*core.InboundHandlerConfig{{Tag: inbound.Tag,
			ReceiverSettings: serial.ToTypedMessage(&proxyman.ReceiverConfig{Listen: address("127.0.0.1"), PortList: &xnet.PortList{Range: []*xnet.PortRange{{From: uint32(inbound.Port), To: uint32(inbound.Port)}}}}),
			ProxySettings:    serial.ToTypedMessage(&dokodemo.Config{AllowedNetworks: []xnet.Network{xnet.Network_UDP}, RewriteAddress: address(inbound.Settings.Address), RewritePort: uint32(inbound.Settings.Port)}),
		}},
		Outbound: []*core.OutboundHandlerConfig{{Tag: outbound.Tag,
			SenderSettings: serial.ToTypedMessage(&proxyman.SenderConfig{StreamSettings: &internet.StreamConfig{
				ProtocolName: "splithttp", SecurityType: tlsMessage.Type, SecuritySettings: []*serial.TypedMessage{tlsMessage},
				TransportSettings: []*internet.TransportConfig{{ProtocolName: "splithttp", Settings: serial.ToTypedMessage(xhttp)}},
			}}),
			ProxySettings: serial.ToTypedMessage(&vlessout.Config{Vnext: &protocol.ServerEndpoint{
				Address: address(outbound.Settings.Address), Port: uint32(outbound.Settings.Port),
				User: &protocol.User{Account: serial.ToTypedMessage(&vless.Account{Id: outbound.Settings.ID, Encryption: "none"})},
			}}),
		}},
	}, nil
}
