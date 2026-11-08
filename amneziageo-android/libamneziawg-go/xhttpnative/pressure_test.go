package xhttpnative

import (
	"context"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/udppressure"
)

func TestPressureOwnerRetirementIsLocal(t *testing.T) {
	var first, second atomic.Uint64
	one := &pressureOwner{sink: func(udppressure.Event) { first.Add(1) }}
	two := &pressureOwner{sink: func(udppressure.Event) { second.Add(1) }}
	event := udppressure.Event{Version: 1, Stage: udppressure.Pipe, DroppedPackets: 1, CapacityBytes: 262144}
	one.emit(event)
	one.close()
	one.emit(event)
	two.emit(event)
	if first.Load() != 1 || second.Load() != 1 {
		t.Fatalf("owner counts %d/%d", first.Load(), second.Load())
	}
	var missing *pressureOwner
	missing.emit(event)
	missing.close()
	(&pressureOwner{}).emit(event)
}

func TestRetiredNativeOwnerDoesNotReceivePendingPressure(t *testing.T) {
	var calls atomic.Uint64
	owner := &pressureOwner{sink: func(udppressure.Event) { calls.Add(1) }}
	reporter := udppressure.New(udppressure.WithSink(context.Background(), owner.emit), func(udppressure.Event) { t.Error("fallback used") })
	owner.close()
	reporter.Drop(udppressure.Pipe, 1, 100, 262144)
	reporter.Close()
	select {
	case <-reporter.Done():
	case <-time.After(time.Second):
		t.Fatal("native reporter did not retire")
	}
	if calls.Load() != 0 {
		t.Fatal("retired native callback was invoked")
	}
}

func TestNativePressureOwnerConcurrentClose(t *testing.T) {
	var calls atomic.Uint64
	owner := &pressureOwner{sink: func(udppressure.Event) { calls.Add(1) }}
	var writers sync.WaitGroup
	for i := 0; i < 4; i++ {
		writers.Add(1)
		go func() {
			defer writers.Done()
			for n := 0; n < 1000; n++ {
				owner.emit(udppressure.Event{Version: 1, Stage: udppressure.Hub})
			}
		}()
	}
	owner.close()
	writers.Wait()
	held := calls.Load()
	owner.emit(udppressure.Event{Version: 1, Stage: udppressure.Hub})
	if calls.Load() != held {
		t.Fatal("new callback began after retirement")
	}
}
