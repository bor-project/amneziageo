package xhttpnative

import (
	"context"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestInvalidSignedCapabilitiesNeverFallBack(t *testing.T) {
	for _, body := range []string{`{"version":1,"maxPostBytes":32768,"uploadWindow":5}`, `{broken`} {
		server := httptest.NewTLSServer(signedTestHandler(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Query().Get("op") != "open" || r.URL.Query().Get("caps") != "1" {
				t.Error("invalid negotiation")
			}
			w.Header().Set("X-WG-Version", "2")
			w.Header().Set("X-WG-Seq", "0")
			w.Write([]byte(body))
		})))
		h, _ := startTestHost(t, server)
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		err := h.WaitReady(ctx)
		cancel()
		server.Close()
		if err == nil || !strings.Contains(err.Error(), "capabilities") || h.IsRunning() {
			t.Fatalf("accepted invalid capabilities: %v", err)
		}
	}
}

func TestUploadCapabilitiesKeepLegacyAndGETLimits(t *testing.T) {
	for _, method := range []string{"GET", "POST"} {
		h := &Host{method: method}
		if err := h.capabilities(nil); err != nil || h.uploadWindow != 1 || h.batchLimit() != 4096 {
			t.Fatal("legacy open changed", err)
		}
		if err := h.capabilities([]byte(`{"version":1,"maxPostBytes":32768,"uploadWindow":4}`)); err != nil {
			t.Fatal(err)
		}
		want := 4096
		if method == "POST" {
			want = 32768
		}
		if h.batchLimit() != want || h.uploadWindow != 4 {
			t.Fatal("invalid limits")
		}
	}
	for _, data := range []string{`null`, `{}`, `[]`, `{"version":2,"maxPostBytes":32768,"uploadWindow":4}`,
		`{"version":1,"maxPostBytes":32769,"uploadWindow":4}`, `{"version":1,"maxPostBytes":4095,"uploadWindow":4}`,
		`{"version":1,"maxPostBytes":4096,"uploadWindow":5}`, `{"version":1,"maxPostBytes":4096,"uploadWindow":0}`,
		`{"version":1,"maxPostBytes":4096,"uploadWindow":4} {}`, `{"version":1,"maxPostBytes":4096,"uploadWindow":4,"extra":true}`} {
		if (&Host{}).capabilities([]byte(data)) == nil {
			t.Fatalf("accepted %s", data)
		}
	}
}

func TestWindowProgressRejectsFutureAndKeepsCumulativeACK(t *testing.T) {
	p := &uploadProgress{changed: make(chan struct{})}
	p.assigned.Store(4)
	if p.acknowledge(3) != nil || p.acknowledge(1) != nil || p.ack != 3 || p.acknowledge(5) == nil {
		t.Fatal("invalid cumulative acknowledgement")
	}
}

func TestUploadWindowReordersRetriesAndStopsWorkers(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var mu sync.Mutex
	pending := map[uint64][]byte{}
	previous := uint64(0)
	var packets atomic.Int32
	second := make(chan struct{})
	firstCancelled := make(chan struct{})
	var once sync.Once
	server := httptest.NewTLSServer(signedTestHandler(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.ContentLength > 0 || r.Header.Get("X-WG-Data") == "" {
			t.Error("GET envelope changed")
		}
		sequence, _ := strconv.ParseUint(r.URL.Query().Get("seq"), 10, 64)
		if sequence == 1 {
			select {
			case <-second:
			case <-r.Context().Done():
				return
			}
		} else {
			once.Do(func() { close(second) })
		}
		data, err := base64.StdEncoding.DecodeString(r.Header.Get("X-WG-Data"))
		if err != nil || len(data) > maxUpload {
			t.Error("invalid upload batch")
		}
		mu.Lock()
		if sequence > previous {
			pending[sequence] = data
		}
		for {
			body, found := pending[previous+1]
			if !found {
				break
			}
			frames, err := parseFrames(body)
			if err != nil {
				t.Error(err)
			}
			packets.Add(int32(len(frames)))
			delete(pending, previous+1)
			previous++
		}
		w.Header().Set("X-WG-Version", "2")
		w.Header().Set("X-WG-Seq", strconv.FormatUint(previous, 10))
		mu.Unlock()
		if sequence == 1 {
			<-r.Context().Done()
			close(firstCancelled)
		}
	})))
	defer server.Close()
	address, _ := url.Parse(server.URL + "/native/")
	auth, _ := newAuthentication(authFixture)
	h := &Host{ctx: ctx, client: server.Client(), base: *address, authentication: auth, session: "0123456789abcdef0123456789abcdef", queue: make(chan []byte, 128), uploadWindow: 4}
	for i := 0; i < 100; i++ {
		h.queue <- make([]byte, 1400)
	}
	ended := make(chan error, 1)
	go func() { ended <- h.upload() }()
	deadline := time.Now().Add(5 * time.Second)
	for packets.Load() < 100 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	select {
	case <-firstCancelled:
	case <-time.After(2 * time.Second):
		t.Error("cumulative ACK did not cancel the lost response")
	}
	cancel()
	select {
	case <-ended:
	case <-time.After(2 * time.Second):
		t.Fatal("upload workers did not stop")
	}
	if packets.Load() != 100 {
		t.Fatalf("received %d/100 datagrams", packets.Load())
	}
}
