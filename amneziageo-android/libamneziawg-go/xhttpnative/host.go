package xhttpnative

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
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
	"sync/atomic"
	"syscall"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/udppressure"
)

const Version = "1"
const maxDatagram = 2048
const maxUpload = 4096
const uploadQueuePackets = 1024

var ioEOF = io.EOF
var protector atomic.Pointer[func(int) bool]
var ErrProtect = errors.New("http-get-protect-failed")
var ErrStart = errors.New("http-get-start-failed")

type pressureOwner struct {
	closed atomic.Bool
	sink   udppressure.Sink
}

func (p *pressureOwner) emit(event udppressure.Event) {
	if p != nil && !p.closed.Load() && p.sink != nil {
		p.sink(event)
	}
}
func (p *pressureOwner) close() {
	if p != nil {
		p.closed.Store(true)
	}
}

type Host struct {
	ctx            context.Context
	cancel         context.CancelFunc
	socket         *net.UDPConn
	client         *http.Client
	transport      *http.Transport
	base           url.URL
	method         string
	ready          chan struct{}
	credential     string
	authentication *authentication
	targetHost     string
	targetPort     int
	session        string
	queue          chan []byte
	done           chan struct{}
	stop           sync.Once
	peerMu         sync.Mutex
	peer           *net.UDPAddr
	connections    atomic.Int32
	fault          error
	dropped        atomic.Uint64
	pressure       *udppressure.Reporter
	pressureOwner  *pressureOwner
	diagnostic     func(string)
}

type trackedConn struct {
	net.Conn
	count *atomic.Int32
	once  sync.Once
}

func (c *trackedConn) Close() error {
	err := c.Conn.Close()
	c.once.Do(func() { c.count.Add(-1) })
	return err
}

func InstallProtector(protect func(int) bool) bool {
	return protect != nil && protector.CompareAndSwap(nil, &protect)
}

func Start(data []byte) (*Host, error) { return StartWithPressure(data, nil) }

func StartWithPressure(data []byte, sink udppressure.Sink) (*Host, error) {
	return StartWithDiagnostics(data, sink, nil)
}

func StartWithDiagnostics(data []byte, sink udppressure.Sink, reporter func(string)) (*Host, error) {
	prepared, err := validateProfile(data)
	if err != nil {
		return nil, err
	}
	if protector.Load() == nil {
		return nil, ErrProtect
	}
	var value profile
	if err := json.Unmarshal(prepared, &value); err != nil {
		return nil, ErrProfile
	}
	inbound, outbound := value.Inbounds[0], value.Outbounds[0]
	authState, err := newAuthentication(outbound.Settings.ID)
	if err != nil {
		return nil, ErrProfile
	}
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: inbound.Port})
	if err != nil {
		return nil, ErrStart
	}
	if err := socket.SetReadBuffer(2 * 1024 * 1024); err != nil {
		socket.Close()
		return nil, ErrStart
	}
	var random [16]byte
	if _, err := rand.Read(random[:]); err != nil {
		socket.Close()
		return nil, ErrStart
	}
	ctx, cancel := context.WithCancel(context.Background())
	h := &Host{ctx: ctx, cancel: cancel, socket: socket, method: outbound.Stream.XHTTP.Extra.Method, ready: make(chan struct{}), credential: outbound.Settings.ID, authentication: authState,
		targetHost: inbound.Settings.Address, targetPort: inbound.Settings.Port,
		session: hex.EncodeToString(random[:]), queue: make(chan []byte, uploadQueuePackets), done: make(chan struct{}), diagnostic: reporter}

	owner := &pressureOwner{sink: sink}
	if sink == nil {
		owner.sink = func(event udppressure.Event) {
			log.Print(event.String())
			if reporter != nil {
				reporter(event.String())
			}
		}
	}
	h.pressureOwner = owner
	h.pressure = udppressure.New(udppressure.WithSink(ctx, owner.emit), nil)
	scheme := "https"
	if outbound.Stream.Security == "none" {
		scheme = "http"
	}
	h.base = url.URL{Scheme: scheme, Host: net.JoinHostPort(outbound.Stream.XHTTP.Host, strconv.Itoa(outbound.Settings.Port)), Path: outbound.Stream.XHTTP.Path}
	tlsConfig := &tls.Config{ServerName: outbound.Stream.TLS.Name, MinVersion: tls.VersionTLS12, NextProtos: []string{"http/1.1"}}
	if outbound.Stream.TLS.Pin != "" {
		pin, _ := hex.DecodeString(outbound.Stream.TLS.Pin)
		tlsConfig.InsecureSkipVerify = true
		tlsConfig.VerifyConnection = func(state tls.ConnectionState) error {
			if len(state.PeerCertificates) == 0 {
				return errCertificateMissing
			}
			digest := sha256.Sum256(state.PeerCertificates[0].Raw)
			if subtle.ConstantTimeCompare(pin, digest[:]) != 1 {
				return errCertificatePin
			}
			return nil
		}
	}
	dialer := net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second, Control: func(_, _ string, raw syscall.RawConn) error {
		accepted := false
		if err := raw.Control(func(fd uintptr) { p := protector.Load(); accepted = p != nil && (*p)(int(fd)) }); err != nil {
			return ErrProtect
		}
		if !accepted {
			return ErrProtect
		}
		return nil
	}}
	dialAddress := net.JoinHostPort(outbound.Settings.Address, strconv.Itoa(outbound.Settings.Port))
	if reporter != nil {
		reporter("HTTP GET TCP front " + dialAddress)
	}
	protocols := new(http.Protocols)
	protocols.SetHTTP1(true)
	h.transport = &http.Transport{Proxy: nil, TLSClientConfig: tlsConfig, Protocols: protocols, ForceAttemptHTTP2: false,
		MaxResponseHeaderBytes: 16384, MaxConnsPerHost: 3, MaxIdleConns: 3, MaxIdleConnsPerHost: 3, IdleConnTimeout: 30 * time.Second,
		TLSHandshakeTimeout: tlsHandshakeTimeout, ResponseHeaderTimeout: 8 * time.Second, DisableCompression: true,
		DialContext: func(ctx context.Context, network, _ string) (net.Conn, error) {
			connection, err := dialer.DialContext(ctx, network, dialAddress)
			if err != nil {
				return nil, err
			}
			h.connections.Add(1)
			return &trackedConn{Conn: connection, count: &h.connections}, nil
		}}
	h.client = &http.Client{Transport: h.transport, Timeout: requestTimeout,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	go h.run()
	return h, nil
}

func (h *Host) request(ctx context.Context, operation string, values url.Values, payload []byte) ([]byte, uint64, int, error) {
	return h.signedRequest(ctx, operation, values, payload)
}

func (h *Host) run() {
	defer close(h.done)
	defer func() { h.pressureOwner.close(); h.pressure.Close(); <-h.pressure.Done() }()
	defer h.transport.CloseIdleConnections()
	defer h.socket.Close()
	defer h.cancel()
	_, _, _, err := h.retry("open", url.Values{"host": {h.targetHost}, "port": {strconv.Itoa(h.targetPort)}}, nil)
	if err != nil {
		if h.ctx.Err() == nil {
			h.reportFault(err)
		}
		return
	}
	close(h.ready)
	if h.diagnostic != nil {
		h.diagnostic("HTTP " + h.httpMethod() + " session opened")
	}
	ended := make(chan error, 3)
	go func() { ended <- h.receiveUDP() }()
	go func() { ended <- h.upload() }()
	go func() { ended <- h.download() }()
	err = <-ended
	if h.ctx.Err() == nil {
		h.reportFault(err)
	}
	h.cancel()
	h.socket.Close()
	h.transport.CloseIdleConnections()
	<-ended
	<-ended
}

func appendFrame(batch []byte, packet []byte) []byte {
	batch = binary.BigEndian.AppendUint16(batch, uint16(len(packet)))
	return append(batch, packet...)
}

func (h *Host) receiveUDP() error {
	buffer := make([]byte, 65536)
	for {
		count, peer, err := h.socket.ReadFromUDP(buffer)
		if err != nil {
			return err
		}
		if count == 0 || count > maxDatagram {
			return errors.New("http-get-datagram-limit")
		}
		h.peerMu.Lock()
		changed := h.peer != nil && h.peer.String() != peer.String()
		h.peer = peer
		h.peerMu.Unlock()
		if changed && h.diagnostic != nil {
			h.diagnostic("HTTP GET local UDP peer rebound")
		}
		packet := append([]byte(nil), buffer[:count]...)
		select {
		case h.queue <- packet:
		case <-h.ctx.Done():
			return h.ctx.Err()

		default:
			h.dropped.Add(1)
			h.pressure.Drop(udppressure.Hub, 1, uint64(count), uint64(cap(h.queue)))
		}
	}
}

func (h *Host) upload() error {
	var carry []byte
	for sequence := uint64(1); ; sequence++ {
		packet := carry
		carry = nil
		if packet == nil {
			select {
			case packet = <-h.queue:
			case <-h.ctx.Done():
				return h.ctx.Err()
			}
		}
		batch := appendFrame(make([]byte, 0, maxUpload), packet)
		timer := time.NewTimer(time.Millisecond)
	collect:
		for {
			select {
			case packet = <-h.queue:
				if len(batch)+len(packet)+2 > maxUpload {
					carry = packet
					break collect
				}
				batch = appendFrame(batch, packet)
			case <-timer.C:
				break collect
			case <-h.ctx.Done():
				timer.Stop()
				return h.ctx.Err()
			}
		}
		timer.Stop()
		_, ack, _, err := h.retry("push", url.Values{"seq": {strconv.FormatUint(sequence, 10)}}, batch)
		if err != nil {
			return err
		}
		if ack != sequence {
			return errors.New("http-get-upload-ack")
		}
	}
}

func parseFrames(data []byte) ([][]byte, error) {
	var result [][]byte
	for len(data) > 0 {
		if len(data) < 2 {
			return nil, errors.New("http-get-frame-header")
		}
		size := int(binary.BigEndian.Uint16(data))
		data = data[2:]
		if size == 0 || size > maxDatagram || size > len(data) {
			return nil, errors.New("http-get-frame-length")
		}
		result = append(result, data[:size])
		data = data[size:]
	}
	if len(result) == 0 {
		return nil, errors.New("http-get-empty-batch")
	}
	return result, nil
}

func (h *Host) download() error {
	ack := uint64(0)
	for {
		data, sequence, status, err := h.retry("pull", url.Values{"ack": {strconv.FormatUint(ack, 10)}}, nil)
		if err != nil {
			return err
		}
		if status == http.StatusNoContent {
			if sequence != ack || len(data) != 0 {
				return errors.New("http-get-idle-ack")
			}
			continue
		}
		if sequence != ack+1 {
			return errors.New("http-get-download-sequence")
		}
		packets, err := parseFrames(data)
		if err != nil {
			return err
		}
		h.peerMu.Lock()
		peer := h.peer
		h.peerMu.Unlock()
		if peer == nil {
			return errors.New("http-get-local-peer-missing")
		}
		for _, packet := range packets {
			if _, err := h.socket.WriteToUDP(packet, peer); err != nil {
				return err
			}
		}
		ack = sequence
	}
}

func (h *Host) Stop(ctx context.Context) error {
	h.stop.Do(func() { h.cancel(); h.socket.Close(); h.transport.CloseIdleConnections() })
	select {
	case <-h.done:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}
func (h *Host) IsRunning() bool { return h.ctx.Err() == nil }
func (h *Host) PoolCount() int  { return int(h.connections.Load()) }
func (h *Host) Wait() error     { <-h.done; return h.fault }

func (h *Host) reportFault(err error) {
	h.fault = err
	log.Printf("HTTP GET transport failed: %v", err)
	if h.diagnostic != nil {
		h.diagnostic("HTTP GET failure: " + err.Error())
	}
}

func (h *Host) httpMethod() string {
	if h.method == "POST" {
		return "POST"
	}
	return "GET"
}

func (h *Host) WaitReady(ctx context.Context) error {
	select {
	case <-h.ready:
		return nil
	case <-h.done:
		return h.fault
	case <-ctx.Done():
		return ctx.Err()
	}
}

func ProtectSocket(fd uintptr) error {
	p := protector.Load()
	if p == nil || !(*p)(int(fd)) {
		return ErrProtect
	}
	return nil
}
