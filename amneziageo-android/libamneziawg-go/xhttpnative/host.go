package xhttpnative

import (
	"context"
	"errors"
	"io"
	"sync"
	"sync/atomic"
	"syscall"

	_ "github.com/xtls/xray-core/app/dispatcher"
	_ "github.com/xtls/xray-core/app/log"
	_ "github.com/xtls/xray-core/app/proxyman/inbound"
	_ "github.com/xtls/xray-core/app/proxyman/outbound"
	"github.com/xtls/xray-core/core"
	_ "github.com/xtls/xray-core/proxy/dokodemo"
	_ "github.com/xtls/xray-core/proxy/vless/outbound"
	"github.com/xtls/xray-core/transport/internet"
	"github.com/xtls/xray-core/transport/internet/splithttp"
	_ "github.com/xtls/xray-core/transport/internet/tls"
)

const Version = "26.10.31"

var ioEOF = io.EOF
var controller sync.Once
var startGate sync.Mutex
var protector atomic.Pointer[func(int) bool]
var ErrProtect = errors.New("xhttp-protect-failed")
var ErrStart = errors.New("xhttp-start-failed")

type Host struct {
	instance *core.Instance
	owner    *splithttp.NativePoolOwner
	stop     sync.Once
	done     chan struct{}
}

func InstallProtector(protect func(int) bool) bool {
	if protect == nil {
		return false
	}
	installed := false
	controller.Do(func() {
		err := internet.RegisterDialerController(func(network, address string, raw syscall.RawConn) error {
			current := protector.Load()
			if current == nil {
				return ErrProtect
			}
			accepted := false
			if err := raw.Control(func(fd uintptr) { accepted = (*current)(int(fd)) }); err != nil {
				return ErrProtect
			}
			if !accepted {
				return ErrProtect
			}
			return nil
		})
		if err == nil {
			protector.Store(&protect)
			installed = true
		}
	})
	return installed
}

func Start(data []byte) (*Host, error) {
	prepared, err := validateProfile(data)
	if err != nil {
		return nil, err
	}
	if protector.Load() == nil {
		return nil, ErrProtect
	}
	config, err := buildConfig(prepared)
	if err != nil {
		return nil, ErrProfile
	}
	owner := splithttp.NewNativePoolOwner()
	startGate.Lock()
	defer startGate.Unlock()
	instance, err := core.NewWithContext(owner.Context(), config)
	if err != nil {
		owner.Close(context.Background())
		return nil, ErrStart
	}
	if err = instance.Start(); err != nil {
		owner.BeginClose()
		instance.Close()
		owner.Close(context.Background())
		return nil, ErrStart
	}
	return &Host{instance: instance, owner: owner, done: make(chan struct{})}, nil
}

func (h *Host) Stop(ctx context.Context) error {
	h.stop.Do(func() {
		go func() {
			defer close(h.done)
			h.owner.BeginClose()
			h.instance.Close()
			h.owner.Close(context.Background())
		}()
	})
	select {
	case <-h.done:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (h *Host) PoolCount() int { return h.owner.PoolCount() }
