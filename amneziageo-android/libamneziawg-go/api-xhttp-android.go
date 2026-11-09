//go:build android

package main

/*
#include <stdint.h>
typedef int (*ag_xhttp_protect_fn)(int fd);
static int ag_xhttp_protect(ag_xhttp_protect_fn fn, int fd) { return fn(fd); }
*/
import "C"

import (
	"context"
	"fmt"
	"sync"
	"syscall"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/udppressure"
	"github.com/bor-project/amneziageo/libamneziawg-go/xhttpnative"
)

var xhttpHandles = struct {
	sync.Mutex
	next  int32
	hosts map[int32]*xhttpnative.Host
}{hosts: make(map[int32]*xhttpnative.Host)}

var xhttpProtector struct {
	sync.Mutex
	callback C.ag_xhttp_protect_fn
}

//export agXHttpStart
func agXHttpStart(profile *C.char, protect C.ag_xhttp_protect_fn) C.int32_t {
	if profile == nil || protect == nil {
		return -1
	}
	xhttpProtector.Lock()
	if xhttpProtector.callback == nil {
		xhttpProtector.callback = protect
		xhttpnative.InstallProtector(func(fd int) bool {
			accepted := C.ag_xhttp_protect(protect, C.int(fd)) != 0
			mark, err := syscall.GetsockoptInt(fd, syscall.SOL_SOCKET, syscall.SO_MARK)
			warnXHttpPressure(fmt.Sprintf("HTTP GET socket protected=%t mark=%#x markError=%t", accepted, mark, err != nil))
			return accepted
		})
	}
	accepted := xhttpProtector.callback == protect
	xhttpProtector.Unlock()
	if !accepted {
		return -3
	}
	host, err := xhttpnative.StartWithDiagnostics([]byte(C.GoString(profile)), func(event udppressure.Event) {
		warnXHttpPressure(event.String())
	}, func(message string) {
		warnXHttpPressure(message)
	})
	if err != nil {
		return -2
	}
	xhttpHandles.Lock()
	defer xhttpHandles.Unlock()
	xhttpHandles.next++
	handle := xhttpHandles.next
	xhttpHandles.hosts[handle] = host
	return C.int32_t(handle)
}

//export agXHttpStop
func agXHttpStop(handle C.int32_t, waitMs C.int32_t) C.int32_t {
	xhttpHandles.Lock()
	host := xhttpHandles.hosts[int32(handle)]
	xhttpHandles.Unlock()
	if host == nil {
		return 0
	}
	wait := time.Duration(waitMs) * time.Millisecond
	if wait < time.Millisecond || wait > 10*time.Second {
		wait = 3 * time.Second
	}
	ctx, cancel := context.WithTimeout(context.Background(), wait)
	defer cancel()
	if host.Stop(ctx) != nil {
		return 1
	}
	xhttpHandles.Lock()
	delete(xhttpHandles.hosts, int32(handle))
	xhttpHandles.Unlock()
	return 0
}

//export agXHttpIsRunning
func agXHttpIsRunning(handle C.int32_t) C.int32_t {
	xhttpHandles.Lock()
	host := xhttpHandles.hosts[int32(handle)]
	xhttpHandles.Unlock()
	if host != nil && host.IsRunning() {
		return 1
	}
	return 0
}
