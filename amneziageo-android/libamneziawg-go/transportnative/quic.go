package transportnative

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"sync"
	"syscall"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/udppressure"
	"github.com/bor-project/amneziageo/libamneziawg-go/xhttpnative"
	"github.com/quic-go/quic-go"
	"github.com/quic-go/quic-go/http3"
	"github.com/quic-go/quic-go/quicvarint"
)

const maxDatagram = 2048
const webTransportStream = 0x41
const enableWebTransport = 0x2b603742
const maxWebTransportSessions = 0xc671706a
const firstStreamError = 0x52e4a40fa8db

type quicConnection struct {
	conn      *quic.Conn
	transport *quic.Transport
	socket    net.PacketConn
	http      *http3.Transport
	request   *http3.RequestStream
}

func (c *quicConnection) close() {
	c.conn.CloseWithError(0, "")
	c.http.Close()
	c.transport.Close()
	c.socket.Close()
}

func dialQUIC(ctx context.Context, p Profile) (*quicConnection, error) {
	tlsConfig := &tls.Config{ServerName: p.Front.TLS.ServerName, MinVersion: tls.VersionTLS13, NextProtos: []string{http3.NextProtoH3}}
	if p.Front.TLS.Pin != "" {
		pin, _ := hex.DecodeString(p.Front.TLS.Pin)
		tlsConfig.InsecureSkipVerify = true
		tlsConfig.VerifyConnection = func(s tls.ConnectionState) error {
			if len(s.PeerCertificates) == 0 {
				return proxyFault("proxy-tls")
			}
			actual := sha256.Sum256(s.PeerCertificates[0].Raw)
			if subtle.ConstantTimeCompare(pin, actual[:]) != 1 {
				return proxyFault("proxy-tls")
			}
			return nil
		}
	}
	listen := net.ListenConfig{Control: func(_, _ string, raw syscall.RawConn) error {
		var result error
		if err := raw.Control(func(fd uintptr) { result = xhttpnative.ProtectSocket(fd) }); err != nil {
			return err
		}
		return result
	}}
	network, bind := "udp4", "0.0.0.0:0"
	if net.ParseIP(p.Front.DialAddress).To4() == nil {
		network, bind = "udp6", "[::]:0"
	}
	socket, err := listen.ListenPacket(ctx, network, bind)
	if err != nil {
		return nil, err
	}
	transport := &quic.Transport{Conn: socket}
	success := false
	defer func() {
		if !success {
			transport.Close()
			socket.Close()
		}
	}()
	remote := &net.UDPAddr{IP: net.ParseIP(p.Front.DialAddress), Port: p.Front.Port}
	conn, err := transport.Dial(ctx, remote, tlsConfig, &quic.Config{EnableDatagrams: true,
		HandshakeIdleTimeout: 10 * time.Second, MaxIdleTimeout: 45 * time.Second, KeepAlivePeriod: 10 * time.Second,
		MaxIncomingStreams: -1, MaxIncomingUniStreams: 3})
	if err != nil {
		return nil, err
	}
	stopDeadline := context.AfterFunc(ctx, func() { conn.CloseWithError(0, "") })
	defer stopDeadline()
	tr := &http3.Transport{EnableDatagrams: true, MaxResponseHeaderBytes: 16384,
		AdditionalSettings: map[uint64]uint64{enableWebTransport: 1, maxWebTransportSessions: 1}}
	defer func() {
		if !success {
			conn.CloseWithError(0, "")
			tr.Close()
		}
	}()
	client := tr.NewClientConn(conn)
	select {
	case <-client.ReceivedSettings():
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-conn.Context().Done():
		return nil, context.Cause(conn.Context())
	}
	settings := client.Settings()
	if settings == nil || !settings.EnableExtendedConnect || !settings.EnableDatagrams ||
		settings.Other[enableWebTransport] != 1 && settings.Other[maxWebTransportSessions] == 0 {
		return nil, proxyFault("proxy-bad-answer")
	}
	requestStream, err := client.OpenRequestStream(ctx)
	if err != nil {
		return nil, err
	}
	address := &url.URL{Scheme: "https", Host: net.JoinHostPort(p.Front.Host, strconv.Itoa(p.Front.Port)), Path: "/" + p.Front.Path + "/events"}
	request := (&http.Request{Method: http.MethodConnect, Proto: "webtransport", Host: address.Host, URL: address,
		Header: http.Header{"Authorization": []string{"Bearer " + p.Auth.Credential}}}).WithContext(ctx)
	if err := requestStream.SendRequestHeader(request); err != nil {
		return nil, err
	}
	response, err := requestStream.ReadResponse()
	if err != nil {
		return nil, connectFault(err)
	}
	if response.StatusCode != http.StatusOK {
		if response.StatusCode == 400 || response.StatusCode == 401 || response.StatusCode == 403 {
			return nil, proxyFault("proxy-auth")
		}
		return nil, proxyFault("proxy-bad-answer")
	}
	success = true
	return &quicConnection{conn: conn, transport: transport, socket: socket, http: tr, request: requestStream}, nil
}

func tunnelPreamble(target Address) ([]byte, error) {
	var random [32]byte
	if _, err := rand.Read(random[:]); err != nil {
		return nil, err
	}
	identity := hex.EncodeToString(random[:16])
	identity = identity[:8] + "-" + identity[8:12] + "-" + identity[12:16] + "-" + identity[16:20] + "-" + identity[20:]
	metadata, err := json.Marshal(map[string]any{"id": identity,
		"p": map[string]any{"Udp": map[string]any{"timeout": nil}}, "r": target.Host, "rp": target.Port})
	if err != nil {
		return nil, err
	}
	token := base64.RawURLEncoding.EncodeToString([]byte(`{"alg":"HS256","typ":"JWT"}`)) + "." + base64.RawURLEncoding.EncodeToString(metadata)
	mac := hmac.New(sha256.New, random[:])
	mac.Write([]byte(token))
	token += "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	return append(binary.BigEndian.AppendUint16(nil, uint16(len(token))), token...), nil
}

func connectFault(err error) error {
	var closed *quic.ApplicationError
	if errors.As(err, &closed) && closed.Remote && closed.ErrorCode == 0 {
		return proxyFault("proxy-rejected")
	}
	return err
}

func streamFault(err error) error {
	var reset *quic.StreamError
	if errors.As(err, &reset) {
		code := uint64(reset.ErrorCode)
		if code >= firstStreamError {
			code -= firstStreamError
			code -= code / 31
		}
		if code == 400 || code == 403 {
			return proxyFault("proxy-target-denied")
		}
	}
	return err
}

type quicHost struct {
	ctx      context.Context
	cancel   context.CancelFunc
	socket   *net.UDPConn
	profile  Profile
	queue    chan []byte
	ready    chan struct{}
	done     chan struct{}
	fault    error
	stop     sync.Once
	peerMu   sync.Mutex
	peer     *net.UDPAddr
	pressure *udppressure.Reporter
}

func startQUIC(p Profile, sink udppressure.Sink) (*quicHost, error) {
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: p.Listen.Port})
	if err != nil {
		return nil, err
	}
	if err := socket.SetReadBuffer(2 * 1024 * 1024); err != nil {
		socket.Close()
		return nil, err
	}
	ctx, cancel := context.WithCancel(context.Background())
	if sink == nil {
		sink = func(event udppressure.Event) { log.Print(event.String()) }
	}
	h := &quicHost{ctx: ctx, cancel: cancel, socket: socket, profile: p, queue: make(chan []byte, 1024), ready: make(chan struct{}), done: make(chan struct{})}
	h.pressure = udppressure.New(udppressure.WithSink(ctx, sink), nil)
	go h.run()
	return h, nil
}

func (h *quicHost) run() {
	defer close(h.done)
	defer func() { h.cancel(); h.socket.Close(); h.pressure.Close(); <-h.pressure.Done() }()
	deadline, cancel := context.WithTimeout(h.ctx, 15*time.Second)
	connection, err := dialQUIC(deadline, h.profile)
	cancel()
	if err != nil {
		h.fault = err
		return
	}
	defer connection.close()
	stopConnection := context.AfterFunc(h.ctx, connection.close)
	defer stopConnection()
	stream, err := connection.conn.OpenStreamSync(h.ctx)
	if err != nil {
		h.fault = streamFault(err)
		return
	}
	preamble, err := tunnelPreamble(h.profile.Target)
	if err == nil {
		header := quicvarint.Append(nil, webTransportStream)
		header = quicvarint.Append(header, uint64(connection.request.StreamID()))
		_, err = stream.Write(append(header, preamble...))
	}
	if err != nil {
		h.fault = streamFault(err)
		return
	}
	close(h.ready)
	ended := make(chan error, 3)
	go func() { ended <- h.receiveUDP() }()
	go func() { ended <- h.upload(stream) }()
	go func() { ended <- h.download(stream) }()
	h.fault = streamFault(<-ended)
	h.cancel()
	h.socket.Close()
	connection.close()
	<-ended
	<-ended
}

func (h *quicHost) receiveUDP() error {
	buffer := make([]byte, 65536)
	for {
		count, peer, err := h.socket.ReadFromUDP(buffer)
		if err != nil {
			return err
		}
		if count == 0 || count > maxDatagram {
			return proxyFault("proxy-datagram-limit")
		}
		h.peerMu.Lock()
		h.peer = peer
		h.peerMu.Unlock()
		packet := append([]byte(nil), buffer[:count]...)
		select {
		case h.queue <- packet:
		case <-h.ctx.Done():
			return h.ctx.Err()
		default:
			h.pressure.Drop(udppressure.Hub, 1, uint64(count), uint64(cap(h.queue)))
		}
	}
}

func (h *quicHost) upload(stream io.Writer) error {
	for {
		select {
		case packet := <-h.queue:
			frame := append(binary.BigEndian.AppendUint16(nil, uint16(len(packet))), packet...)
			if _, err := stream.Write(frame); err != nil {
				return err
			}
		case <-h.ctx.Done():
			return h.ctx.Err()
		}
	}
}

func (h *quicHost) download(stream io.Reader) error {
	var header [2]byte
	buffer := make([]byte, maxDatagram)
	for {
		if _, err := io.ReadFull(stream, header[:]); err != nil {
			return err
		}
		length := int(binary.BigEndian.Uint16(header[:]))
		if length == 0 || length > maxDatagram {
			return proxyFault("proxy-bad-answer")
		}
		if _, err := io.ReadFull(stream, buffer[:length]); err != nil {
			return err
		}
		h.peerMu.Lock()
		peer := h.peer
		h.peerMu.Unlock()
		if peer == nil {
			return proxyFault("proxy-bad-answer")
		}
		if _, err := h.socket.WriteToUDP(buffer[:length], peer); err != nil {
			return err
		}
	}
}

func (h *quicHost) WaitReady(ctx context.Context) error {
	select {
	case <-h.ready:
		return nil
	case <-h.done:
		return h.fault
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (h *quicHost) Wait() error { <-h.done; return h.fault }
func (h *quicHost) Stop(ctx context.Context) error {
	h.stop.Do(func() { h.cancel(); h.socket.Close() })
	select {
	case <-h.done:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}
