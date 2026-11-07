package xhttpnative

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

var protectedCalls atomic.Int32
var rejectProtect atomic.Bool

func init() {
	InstallProtector(func(int) bool { protectedCalls.Add(1); return !rejectProtect.Load() })
}

func freeUDP(t *testing.T) int {
	t.Helper()
	listener, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	return listener.LocalAddr().(*net.UDPAddr).Port
}

func send(t *testing.T, port int) {
	t.Helper()
	connection, err := net.Dial("udp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	if _, err := connection.Write([]byte("native-lifecycle")); err != nil {
		t.Fatal(err)
	}
}

func TestTwoInstancesCloseTheirOwnHTTPPools(t *testing.T) {
	if InstallProtector(func(int) bool { return false }) {
		t.Fatal("replaced the process protection callback")
	}
	opened := make(chan struct{}, 16)
	closed := make(chan struct{}, 16)
	bad := atomic.Bool{}
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Method != "GET" || request.ProtoMajor != 2 || request.ContentLength > 0 || request.Header.Get("Upgrade") != "" {
			bad.Store(true)
		}
		writer.Header().Set("Cache-Control", "no-store")
		writer.WriteHeader(200)
		writer.(http.Flusher).Flush()
		if strings.Count(strings.Trim(request.URL.Path, "/"), "/") == 1 {
			opened <- struct{}{}
			<-request.Context().Done()
			closed <- struct{}{}
		}
	}))
	server.EnableHTTP2 = true
	server.StartTLS()
	defer server.Close()
	digest := sha256.Sum256(server.Certificate().Raw)
	pin := hex.EncodeToString(digest[:])
	port := server.Listener.Addr().(*net.TCPAddr).Port
	for round := 0; round < 2; round++ {
		firstPort, secondPort := freeUDP(t), freeUDP(t)
		first, err := Start(testProfile(firstPort, port, pin))
		if err != nil {
			t.Fatal(err)
		}
		second, err := Start(testProfile(secondPort, port, pin))
		if err != nil {
			t.Fatal(err)
		}
		send(t, firstPort)
		send(t, secondPort)
		for count := 0; count < 2; count++ {
			select {
			case <-opened:
			case <-time.After(8 * time.Second):
				t.Fatal("no HTTP/2 downlink")
			}
		}
		stopHost(t, first)
		select {
		case <-closed:
		case <-time.After(3 * time.Second):
			t.Fatal("first downlink leaked")
		}
		if first.PoolCount() != 0 || second.PoolCount() == 0 {
			t.Fatal("pool ownership was lost")
		}
		select {
		case <-closed:
			t.Fatal("second instance was canceled")
		case <-time.After(50 * time.Millisecond):
		}
		stopHost(t, second)
		select {
		case <-closed:
		case <-time.After(3 * time.Second):
			t.Fatal("second downlink leaked")
		}
		if second.PoolCount() != 0 {
			t.Fatal("pool retained after close")
		}
	}
	if protectedCalls.Load() == 0 || bad.Load() {
		t.Fatal("transport/protection contract failed")
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

func TestFailedProtectDoesNotDial(t *testing.T) {
	requests := atomic.Int32{}
	previous := protectedCalls.Load()
	rejectProtect.Store(true)
	defer rejectProtect.Store(false)
	server := httptest.NewTLSServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { requests.Add(1) }))
	defer server.Close()
	digest := sha256.Sum256(server.Certificate().Raw)
	localPort := freeUDP(t)
	host, err := Start(testProfile(localPort, server.Listener.Addr().(*net.TCPAddr).Port, hex.EncodeToString(digest[:])))
	if err != nil {
		t.Fatal(err)
	}
	send(t, localPort)
	deadline := time.Now().Add(3 * time.Second)
	for protectedCalls.Load() == previous && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	stopHost(t, host)
	if protectedCalls.Load() == previous || requests.Load() != 0 || host.PoolCount() != 0 {
		t.Fatal("failed protection reached the server or leaked a pool")
	}
}

func TestRotatedHTTPClientsCloseTheirConnections(t *testing.T) {
	uploaded := make(chan struct{}, 8192)
	requests := atomic.Int32{}
	connections := atomic.Int32{}
	live := atomic.Int32{}
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		writer.WriteHeader(200)
		writer.(http.Flusher).Flush()
		if strings.Count(strings.Trim(request.URL.Path, "/"), "/") == 1 {
			<-request.Context().Done()
		} else {
			requests.Add(1)
			uploaded <- struct{}{}
			select {
			case <-time.After(100 * time.Millisecond):
			case <-request.Context().Done():
			}
		}
	}))
	server.Config.ConnState = func(_ net.Conn, state http.ConnState) {
		if state == http.StateNew {
			connections.Add(1)
			live.Add(1)
		}
		if state == http.StateClosed {
			live.Add(-1)
		}
	}
	server.EnableHTTP2 = true
	server.StartTLS()
	defer server.Close()
	digest := sha256.Sum256(server.Certificate().Raw)
	localPort := freeUDP(t)
	host, err := Start(testProfile(localPort, server.Listener.Addr().(*net.TCPAddr).Port, hex.EncodeToString(digest[:])))
	if err != nil {
		t.Fatal(err)
	}
	connection, err := net.Dial("udp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(localPort)))
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	for index := 0; index < 2800; index++ {
		if _, err := connection.Write(make([]byte, 2000)); err != nil {
			t.Fatal(err)
		}
		select {
		case <-uploaded:
		case <-time.After(3 * time.Second):
			t.Fatal("uplink stalled")
		}
	}
	stopHost(t, host)
	deadline := time.Now().Add(3 * time.Second)
	for live.Load() != 0 && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if requests.Load() < 2800 || connections.Load() <= 3 || live.Load() != 0 {
		t.Fatalf("retired transports remain: requests=%d connections=%d live=%d", requests.Load(), connections.Load(), live.Load())
	}
}
