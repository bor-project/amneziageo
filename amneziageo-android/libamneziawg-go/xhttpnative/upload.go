package xhttpnative

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"math"
	"net/url"
	"strconv"
	"sync"
	"sync/atomic"
	"time"
)

func (h *Host) capabilities(data []byte) error {
	h.uploadLimit, h.uploadWindow = maxUpload, 1
	if len(data) == 0 {
		return nil
	}
	var value struct {
		Version int `json:"version"`
		MaxPost int `json:"maxPostBytes"`
		Window  int `json:"uploadWindow"`
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if len(data) > 1024 || decoder.Decode(&value) != nil || decoder.Decode(new(any)) != io.EOF ||
		value.Version != 1 || value.MaxPost < maxUpload || value.MaxPost > maxPostUpload || value.Window < 1 || value.Window > maxUploadWindow {
		return errors.New("http-get-capabilities")
	}
	h.uploadWindow = value.Window
	if h.httpMethod() == "POST" {
		h.uploadLimit = value.MaxPost
	}
	return nil
}

func (h *Host) batchLimit() int {
	if h.httpMethod() == "POST" && h.uploadLimit >= maxUpload && h.uploadLimit <= maxPostUpload {
		return h.uploadLimit
	}
	return maxUpload
}

type uploadTask struct {
	sequence uint64
	data     []byte
}

type uploadProgress struct {
	sync.Mutex
	ack      uint64
	assigned atomic.Uint64
	changed  chan struct{}
}

func (p *uploadProgress) acknowledge(ack uint64) error {
	p.Lock()
	defer p.Unlock()
	if ack > p.assigned.Load() {
		return errors.New("http-get-upload-window-ack")
	}
	if ack > p.ack {
		p.ack = ack
		close(p.changed)
		p.changed = make(chan struct{})
	}
	return nil
}

func (h *Host) sendUpload(ctx context.Context, task uploadTask, progress *uploadProgress) error {
	ctx, cancel := context.WithTimeout(ctx, recoveryBudget)
	finished := make(chan struct{})
	acknowledged := func() bool {
		progress.Lock()
		defer progress.Unlock()
		return progress.ack >= task.sequence
	}
	go func() {
		defer close(finished)
		for {
			progress.Lock()
			done, changed := progress.ack >= task.sequence, progress.changed
			progress.Unlock()
			if done {
				cancel()
				return
			}
			select {
			case <-ctx.Done():
				return
			case <-changed:
			}
		}
	}()
	defer func() { cancel(); <-finished }()
	for {
		if acknowledged() {
			return nil
		}
		_, ack, _, err := h.retryContext(ctx, "push", url.Values{"seq": {strconv.FormatUint(task.sequence, 10)}}, task.data)
		if err != nil {
			if acknowledged() && errors.Is(err, context.Canceled) {
				return nil
			}
			return err
		}
		if err := progress.acknowledge(ack); err != nil {
			return err
		}
		progress.Lock()
		done, changed := progress.ack >= task.sequence, progress.changed
		progress.Unlock()
		if done {
			return nil
		}
		timer := time.NewTimer(100 * time.Millisecond)
		select {
		case <-ctx.Done():
			timer.Stop()
			if acknowledged() {
				return nil
			}
			return ctx.Err()
		case <-changed:
			timer.Stop()
		case <-timer.C:
		}
	}
}

func (h *Host) upload() error {
	if h.uploadWindow <= 1 {
		return h.uploadSerial()
	}
	ctx, cancel := context.WithCancel(h.ctx)
	var workers sync.WaitGroup
	defer func() { cancel(); workers.Wait() }()
	slots := make(chan struct{}, h.uploadWindow)
	failures := make(chan error, 1)
	progress := &uploadProgress{changed: make(chan struct{})}
	ending := func() error {
		select {
		case err := <-failures:
			return err
		default:
			return ctx.Err()
		}
	}
	var carry []byte
	for sequence := uint64(1); ; sequence++ {
		if sequence == math.MaxUint64 {
			return errors.New("http-get-upload-sequence-exhausted")
		}
		select {
		case slots <- struct{}{}:
		case <-ctx.Done():
			return ending()
		}
		packet := carry
		carry = nil
		if packet == nil {
			select {
			case packet = <-h.queue:
			case <-ctx.Done():
				return ending()
			}
		}
		batch := appendFrame(make([]byte, 0, h.batchLimit()), packet)
		timer := time.NewTimer(time.Millisecond)
	collect:
		for {
			select {
			case packet = <-h.queue:
				if len(batch)+len(packet)+2 > h.batchLimit() {
					carry = packet
					break collect
				}
				batch = appendFrame(batch, packet)
			case <-timer.C:
				break collect
			case <-ctx.Done():
				timer.Stop()
				return ending()
			}
		}
		timer.Stop()
		progress.assigned.Store(sequence)
		workers.Add(1)
		go func(task uploadTask) {
			defer workers.Done()
			defer func() { <-slots }()
			if err := h.sendUpload(ctx, task, progress); err != nil {
				select {
				case failures <- err:
				default:
				}
				cancel()
			}
		}(uploadTask{sequence, batch})
	}
}
