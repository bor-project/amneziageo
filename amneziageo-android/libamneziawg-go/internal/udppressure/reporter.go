package udppressure

import (
	"context"
	"encoding/json"
	"math"
	"sync"
	"time"
)

const Marker = "AG_UDP_PRESSURE "
const Interval = 10 * time.Second

type Stage string

const (
	Pipe Stage = "pipe"
	Hub  Stage = "hub"
)

type Event struct {
	Version         int    `json:"v"`
	Stage           Stage  `json:"stage"`
	DroppedPackets  uint64 `json:"droppedPackets"`
	DroppedBytes    uint64 `json:"droppedBytes"`
	TotalPackets    uint64 `json:"totalPackets"`
	TotalBytes      uint64 `json:"totalBytes"`
	CapacityBytes   uint64 `json:"capacityBytes"`
	CapacityPackets uint64 `json:"capacityPackets"`
}

func (e Event) String() string {
	data, _ := json.Marshal(e)
	return Marker + string(data)
}

type Sink func(Event)
type contextKey struct{}

func WithSink(ctx context.Context, sink Sink) context.Context {
	return context.WithValue(ctx, contextKey{}, sink)
}

type counters struct {
	packets, bytes, totalPackets, totalBytes, capacity uint64
	next                                               time.Time
}

type Reporter struct {
	mu      sync.Mutex
	stages  [2]counters
	sink    Sink
	ctx     context.Context
	wake    chan struct{}
	stop    chan struct{}
	done    chan struct{}
	started bool
	closed  bool
}

func New(ctx context.Context, fallback Sink) *Reporter {
	if ctx == nil {
		ctx = context.Background()
	}
	if sink, ok := ctx.Value(contextKey{}).(Sink); ok && sink != nil {
		fallback = sink
	}
	return &Reporter{sink: fallback, ctx: ctx, wake: make(chan struct{}, 1), stop: make(chan struct{}), done: make(chan struct{})}
}

func add(left, right uint64) uint64 {
	if right > math.MaxUint64-left {
		return math.MaxUint64
	}
	return left + right
}

func stageIndex(stage Stage) int {
	switch stage {
	case Pipe:
		return 0
	case Hub:
		return 1
	default:
		return -1
	}
}

func (r *Reporter) Drop(stage Stage, packets, bytes, capacity uint64) {
	index := stageIndex(stage)
	if r == nil || index < 0 || packets == 0 || capacity == 0 || r.ctx.Err() != nil {
		return
	}
	r.mu.Lock()
	if r.closed || r.sink == nil {
		r.mu.Unlock()
		return
	}
	current := &r.stages[index]
	current.packets = add(current.packets, packets)
	current.bytes = add(current.bytes, bytes)
	current.totalPackets = add(current.totalPackets, packets)
	current.totalBytes = add(current.totalBytes, bytes)
	current.capacity = capacity
	if !r.started {
		r.started = true
		go r.run()
	}
	r.mu.Unlock()
	select {
	case r.wake <- struct{}{}:
	default:
	}
}

func (r *Reporter) collect(now time.Time) ([]Event, time.Duration) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closed {
		return nil, 0
	}
	var events []Event
	var wait time.Duration
	for index := range r.stages {
		current := &r.stages[index]
		if current.packets == 0 {
			continue
		}
		if !current.next.IsZero() && now.Before(current.next) {
			delay := current.next.Sub(now)
			if wait == 0 || delay < wait {
				wait = delay
			}
			continue
		}
		event := Event{Version: 1, Stage: Pipe, DroppedPackets: current.packets, DroppedBytes: current.bytes, TotalPackets: current.totalPackets, TotalBytes: current.totalBytes, CapacityBytes: current.capacity}
		if index == 1 {
			event.Stage = Hub
			event.CapacityBytes = 0
			event.CapacityPackets = current.capacity
		}
		events = append(events, event)
		current.packets, current.bytes = 0, 0
		current.next = now.Add(Interval)
	}
	return events, wait
}

func (r *Reporter) deliver(event Event) {
	r.mu.Lock()
	if r.closed {
		r.mu.Unlock()
		return
	}
	sink := r.sink
	r.mu.Unlock()
	sink(event)
}

func (r *Reporter) run() {
	defer close(r.done)
	timer := time.NewTimer(time.Hour)
	if !timer.Stop() {
		<-timer.C
	}
	defer timer.Stop()
	var deadline <-chan time.Time
	for {
		select {
		case <-r.stop:
			return
		case <-r.ctx.Done():
			r.Close()
			return
		case <-r.wake:
		case <-deadline:
		}
		events, wait := r.collect(time.Now())
		for _, event := range events {
			r.deliver(event)
		}
		if !timer.Stop() {
			select {
			case <-timer.C:
			default:
			}
		}
		deadline = nil
		if wait > 0 {
			timer.Reset(wait)
			deadline = timer.C
		}
	}
}

func (r *Reporter) Close() {
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closed {
		return
	}
	r.closed = true
	close(r.stop)
	if !r.started {
		close(r.done)
	}
}

func (r *Reporter) Done() <-chan struct{} {
	return r.done
}
