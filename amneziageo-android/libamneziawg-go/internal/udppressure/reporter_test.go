package udppressure

import (
	"context"
	"encoding/json"
	"math"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestIdleReporterDoesNotStartWorker(t *testing.T) {
	r := New(context.Background(), func(Event) { t.Error("idle callback") })
	if r.started {
		t.Fatal("idle reporter started")
	}
	r.Close()
	select {
	case <-r.Done():
	default:
		t.Fatal("idle reporter did not close")
	}
}

func TestDropStateRatesStagesAndTotals(t *testing.T) {
	r := New(context.Background(), func(Event) {})
	r.started = true
	now := time.Unix(1000, 0)
	r.Drop(Pipe, 2, 200, 262144)
	first, wait := r.collect(now)
	if len(first) != 1 || wait != 0 || first[0].DroppedPackets != 2 || first[0].TotalBytes != 200 || first[0].CapacityPackets != 0 {
		t.Fatalf("first = %+v, wait = %v", first, wait)
	}
	r.Drop(Pipe, 3, 300, 262144)
	r.Drop(Hub, 1, 40, 256)
	next, wait := r.collect(now.Add(5 * time.Second))
	if len(next) != 1 || next[0].Stage != Hub || next[0].CapacityBytes != 0 || next[0].CapacityPackets != 256 || wait != 5*time.Second {
		t.Fatalf("independent stages = %+v, wait = %v", next, wait)
	}
	last, wait := r.collect(now.Add(10 * time.Second))
	if len(last) != 1 || last[0].Stage != Pipe || last[0].DroppedPackets != 3 || last[0].TotalPackets != 5 || last[0].TotalBytes != 500 || wait != 0 {
		t.Fatalf("aggregated = %+v, wait = %v", last, wait)
	}
}

func TestBlockedSinkDoesNotBlockDropOrClose(t *testing.T) {
	entered := make(chan struct{})
	release := make(chan struct{})
	var calls int
	r := New(context.Background(), func(Event) {
		calls++
		close(entered)
		<-release
	})
	r.Drop(Pipe, 1, 1, 262144)
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("first event not emitted")
	}
	added := make(chan struct{})
	go func() {
		for i := 0; i < 1000; i++ {
			r.Drop(Pipe, 1, 100, 262144)
		}
		r.Close()
		close(added)
	}()
	select {
	case <-added:
	case <-time.After(time.Second):
		close(release)
		t.Fatal("producer or Close blocked on sink")
	}
	r.Drop(Hub, 1, 1, 256)
	close(release)
	select {
	case <-r.Done():
	case <-time.After(time.Second):
		t.Fatal("reporter did not retire")
	}
	if calls != 1 {
		t.Fatalf("late callbacks: %d", calls)
	}
}

func TestContextSinksStayPerInstance(t *testing.T) {
	a, b := make(chan Event, 1), make(chan Event, 1)
	one := New(WithSink(context.Background(), func(e Event) { a <- e }), func(Event) { t.Error("fallback used") })
	two := New(WithSink(context.Background(), func(e Event) { b <- e }), func(Event) { t.Error("fallback used") })
	one.Drop(Pipe, 1, 11, 262144)
	select {
	case event := <-a:
		if event.DroppedBytes != 11 {
			t.Fatal("first owner mismatch")
		}
	case <-time.After(time.Second):
		t.Fatal("first sink absent")
	}
	one.Close()
	<-one.Done()
	one.Drop(Pipe, 1, 99, 262144)
	two.Drop(Hub, 1, 22, 256)
	select {
	case event := <-b:
		if event.Stage != Hub || event.DroppedBytes != 22 {
			t.Fatal("second owner mismatch")
		}
	case <-time.After(time.Second):
		t.Fatal("closing first disabled second")
	}
	two.Close()
	<-two.Done()
	select {
	case <-a:
		t.Fatal("retired sink received new event")
	default:
	}
}

func TestCancelledNilAndInvalidDropsHaveNoWorker(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	for _, r := range []*Reporter{New(ctx, func(Event) {}), New(context.Background(), nil)} {
		r.Drop(Pipe, 1, 100, 262144)
		r.Drop(Stage("invalid"), 1, 100, 262144)
		r.Drop(Pipe, 0, 100, 262144)
		r.Drop(Pipe, 1, 100, 0)
		if r.started {
			t.Fatal("invalid drop started worker")
		}
		r.Close()
	}
	var nilReporter *Reporter
	nilReporter.Drop(Pipe, 1, 1, 1)
	nilReporter.Close()
}

func TestCounterSaturationAndNumericWire(t *testing.T) {
	if add(math.MaxUint64-1, 2) != math.MaxUint64 {
		t.Fatal("counter wrapped")
	}
	line := (Event{Version: 1, Stage: Pipe, DroppedPackets: 1, DroppedBytes: 100, TotalPackets: 2, TotalBytes: 200, CapacityBytes: 262144}).String()
	if !strings.HasPrefix(line, Marker) {
		t.Fatal("marker absent")
	}
	var fields map[string]interface{}
	if err := json.Unmarshal([]byte(strings.TrimPrefix(line, Marker)), &fields); err != nil || len(fields) != 8 {
		t.Fatalf("wire fields = %#v, err = %v", fields, err)
	}
	for key, value := range fields {
		if key == "stage" {
			if value != "pipe" {
				t.Fatal("stage differs")
			}
		} else if _, ok := value.(float64); !ok {
			t.Fatalf("nonnumeric field %s", key)
		}
	}
}

func TestConcurrentDropsAndClose(t *testing.T) {
	r := New(context.Background(), func(Event) {})
	var workers sync.WaitGroup
	for i := 0; i < 8; i++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			for n := 0; n < 1000; n++ {
				r.Drop(Pipe, 1, 100, 262144)
				r.Drop(Hub, 1, 100, 256)
			}
		}()
	}
	workers.Wait()
	r.Close()
	select {
	case <-r.Done():
	case <-time.After(time.Second):
		t.Fatal("concurrent reporter did not stop")
	}
}
