package xhttpnative

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

var protectedCalls atomic.Int32
var rejectProtect atomic.Bool

func init() { InstallProtector(func(int) bool { protectedCalls.Add(1); return !rejectProtect.Load() }) }

func freeUDP(t *testing.T) int {
	t.Helper()
	listener, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	return listener.LocalAddr().(*net.UDPAddr).Port
}

type testSession struct {
	mu       sync.Mutex
	upload   uint64
	download uint64
	pending  []byte
	packets  chan []byte
}

func getServer(t *testing.T) (*httptest.Server, *atomic.Int32) { return getServerWithPushGate(t, nil) }

func getServerWithPushGate(t *testing.T, gate <-chan struct{}) (*httptest.Server, *atomic.Int32) {
	t.Helper()
	var sessions sync.Map
	bad := &atomic.Int32{}
	server := httptest.NewTLSServer(signedTestHandler(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Method != "GET" || request.ContentLength > 0 || request.Header.Get("Transfer-Encoding") != "" || request.Header.Get("Upgrade") != "" || request.Header.Get("Authorization") != "" || request.URL.Query().Get("token") != "" {
			bad.Add(1)
			writer.WriteHeader(400)
			return
		}
		writer.Header().Set("X-WG-Version", "1")
		writer.Header().Set("X-WG-Seq", "0")
		writer.Header().Set("Cache-Control", "no-store")
		query := request.URL.Query()
		key := request.Header.Get("X-WG-Key") + "/" + query.Get("s")
		if query.Get("op") == "open" {
			sessions.LoadOrStore(key, &testSession{packets: make(chan []byte, 256)})
			return
		}
		value, exists := sessions.Load(key)
		if !exists {
			writer.WriteHeader(410)
			return
		}
		session := value.(*testSession)
		switch query.Get("op") {
		case "push":
			if gate != nil {
				select {
				case <-gate:
				case <-request.Context().Done():
					return
				}
			}
			data, err := base64.StdEncoding.DecodeString(request.Header.Get("X-WG-Data"))
			if err != nil {
				bad.Add(1)
				writer.WriteHeader(400)
				return
			}
			if _, err := parseFrames(data); err != nil {
				bad.Add(1)
				writer.WriteHeader(400)
				return
			}
			sequence, _ := strconv.ParseUint(query.Get("seq"), 10, 64)
			session.mu.Lock()
			if sequence == session.upload+1 {
				session.upload = sequence
				session.packets <- data
			}
			writer.Header().Set("X-WG-Seq", strconv.FormatUint(session.upload, 10))
			session.mu.Unlock()
		case "pull":
			ack, _ := strconv.ParseUint(query.Get("ack"), 10, 64)
			if ack == session.download {
				session.pending = nil
			}
			if session.pending == nil {
				select {
				case session.pending = <-session.packets:
					session.download++
				case <-time.After(100 * time.Millisecond):
					writer.Header().Set("X-WG-Seq", strconv.FormatUint(session.download, 10))
					writer.WriteHeader(204)
					return
				case <-request.Context().Done():
					return
				}
			}
			writer.Header().Set("X-WG-Seq", strconv.FormatUint(session.download, 10))
			writer.Write(session.pending)
		default:
			writer.WriteHeader(400)
		}
	})))
	return server, bad
}

func startTestHost(t *testing.T, server *httptest.Server) (*Host, int) {
	t.Helper()
	digest := sha256.Sum256(server.Certificate().Raw)
	port := freeUDP(t)
	host, err := Start(testProfile(port, server.Listener.Addr().(*net.TCPAddr).Port, hex.EncodeToString(digest[:])))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { stopHost(t, host) })
	return host, port
}

func roundTrip(t *testing.T, port int, sizes []int) {
	t.Helper()
	connection, err := net.Dial("udp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	if err := connection.SetDeadline(time.Now().Add(5 * time.Second)); err != nil {
		t.Fatal(err)
	}
	for index, size := range sizes {
		packet := bytes.Repeat([]byte{byte(index + 1)}, size)
		if _, err := connection.Write(packet); err != nil {
			t.Fatal(err)
		}
		buffer := make([]byte, 4096)
		count, err := connection.Read(buffer)
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(packet, buffer[:count]) {
			t.Fatal("UDP datagram changed")
		}
	}
}

func TestGETDatagramsRoundTrip(t *testing.T) {
	server, bad := getServer(t)
	defer server.Close()
	_, port := startTestHost(t, server)
	roundTrip(t, port, []int{33, 148, 1280, 2048, 33})
	if bad.Load() != 0 {
		t.Fatal("GET envelope or framing violated")
	}
}

func TestGETSurvivesWireGuardUDPRebind(t *testing.T) {
	server, bad := getServer(t)
	defer server.Close()
	_, port := startTestHost(t, server)
	roundTrip(t, port, []int{148, 1280})
	roundTrip(t, port, []int{148, 1280})
	if bad.Load() != 0 {
		t.Fatal("GET envelope violated after UDP rebind")
	}
}

func TestGETHostsOwnTheirConnections(t *testing.T) {
	if InstallProtector(func(int) bool { return false }) {
		t.Fatal("protector replaced")
	}
	server, _ := getServer(t)
	defer server.Close()
	first, firstPort := startTestHost(t, server)
	second, secondPort := startTestHost(t, server)
	roundTrip(t, firstPort, []int{148})
	roundTrip(t, secondPort, []int{1280})
	stopHost(t, first)
	if first.IsRunning() || !second.IsRunning() || first.PoolCount() != 0 || second.PoolCount() == 0 {
		t.Fatal("connection ownership lost")
	}
	stopHost(t, second)
	if second.PoolCount() != 0 {
		t.Fatal("connection leaked")
	}
}

func TestFailedProtectDoesNotDial(t *testing.T) {
	requests := atomic.Int32{}
	rejectProtect.Store(true)
	defer rejectProtect.Store(false)
	server := httptest.NewTLSServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { requests.Add(1) }))
	defer server.Close()
	host, _ := startTestHost(t, server)
	finished := make(chan error, 1)
	go func() { finished <- host.Wait() }()
	select {
	case err := <-finished:
		if err == nil {
			t.Fatal("failed protection accepted")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("protection failure hung")
	}
	if host.IsRunning() {
		t.Fatal("failed carrier remained running")
	}
	if requests.Load() != 0 {
		t.Fatal("failed protection reached server")
	}
}

func TestGETRejectsWrongProtocol(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.Write([]byte("website")) }))
	defer server.Close()
	host, _ := startTestHost(t, server)
	finished := make(chan error, 1)
	go func() { finished <- host.Wait() }()
	select {
	case err := <-finished:
		if err == nil {
			t.Fatal("website accepted as carrier")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("protocol mismatch hung")
	}
}

func TestGETFramingRejectsBrokenDatagrams(t *testing.T) {
	for _, data := range [][]byte{nil, {0}, {0, 0}, {0, 2, 1}, {8, 1}} {
		if _, err := parseFrames(data); err == nil {
			t.Fatalf("invalid frame accepted: %x", data)
		}
	}
}

func stopHost(t *testing.T, host *Host) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err := host.Stop(ctx); err != nil {
		t.Fatal(err)
	}
	if err := host.Stop(ctx); err != nil {
		t.Fatal(err)
	}
}

func TestGETSurvivesUploadQueuePressure(t *testing.T) {
	gate := make(chan struct{})
	var release sync.Once
	server, _ := getServerWithPushGate(t, gate)
	defer server.Close()
	defer release.Do(func() { close(gate) })
	host, port := startTestHost(t, server)
	connection, err := net.Dial("udp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	for index := 0; index < cap(host.queue)+512; index++ {
		if _, err := connection.Write(bytes.Repeat([]byte{11}, 148)); err != nil {
			t.Fatal(err)
		}
		if index%20 == 0 {
			time.Sleep(time.Millisecond)
		}
	}
	time.Sleep(100 * time.Millisecond)
	select {
	case <-host.done:
		t.Fatalf("UDP burst terminated carrier: %v", host.fault)
	default:
	}
	if host.dropped.Load() == 0 {
		t.Fatal("test did not saturate upload queue")
	}
	release.Do(func() { close(gate) })
	deadline := time.Now().Add(3 * time.Second)
	for len(host.queue) > 0 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	probe := bytes.Repeat([]byte{77}, 1280)
	buffer := make([]byte, 4096)
	connection.SetDeadline(time.Now().Add(5 * time.Second))
	if _, err := connection.Write(probe); err != nil {
		t.Fatal(err)
	}
	for {
		count, err := connection.Read(buffer)
		if err != nil {
			t.Fatalf("carrier did not recover after upload pressure: %v", err)
		}
		if bytes.Equal(buffer[:count], probe) {
			break
		}
	}
}
