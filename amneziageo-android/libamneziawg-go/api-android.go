//go:build android

/* SPDX-License-Identifier: MIT
 *
 * c-shared entry points for the Android VpnService host (AmneziaGeo).
 */

package main

/*
#include <stdlib.h>
#include <android/log.h>
#cgo LDFLAGS: -llog

// Хост защищает наш сокет от туннеля; возвращает ненулевое при успехе.
typedef int (*ag_protect_fn)(int fd);

static int ag_call_protect(ag_protect_fn fn, int fd) { return fn(fd); }

// Хост называет владельца потока: 1 - наш собственный процесс, 2 - приложение из правил, 0 - остальные.
typedef int (*ag_owner_fn)(int proto, unsigned int src, unsigned short srcPort, unsigned int dst, unsigned short dstPort);

static int ag_call_owner(ag_owner_fn fn, int proto, unsigned int src, unsigned short srcPort, unsigned int dst, unsigned short dstPort) {
	return fn(proto, src, srcPort, dst, dstPort);
}
*/
import "C"

import (
	"fmt"
	"time"
	"unsafe"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
)

const logTag = "amneziawg-go"

// Движок вместе со слоем вердиктов, который стоит под ним.
type tunnel struct {
	dev *device.Device
	tun *verdictTun
}

var (
	tunnelHandles = make(map[int32]*tunnel)
	nextHandle    int32
)

func androidLog(prio C.int, tag *C.char, message string) {
	cmessage := C.CString(message)
	C.__android_log_write(prio, tag, cmessage)
	C.free(unsafe.Pointer(cmessage))
}

func newAndroidLogger(level int) *device.Logger {
	tag := C.CString(logTag)
	logger := &device.Logger{Verbosef: device.DiscardLogf, Errorf: device.DiscardLogf}
	if level >= device.LogLevelVerbose {
		logger.Verbosef = func(format string, args ...any) {
			androidLog(C.ANDROID_LOG_DEBUG, tag, fmt.Sprintf(format, args...))
		}
	}
	if level >= device.LogLevelError {
		logger.Errorf = func(format string, args ...any) {
			androidLog(C.ANDROID_LOG_ERROR, tag, fmt.Sprintf(format, args...))
		}
	}
	return logger
}

//export wgTurnOn
func wgTurnOn(settings *C.char, tunFd int32, logLevel int32) int32 {
	logger := newAndroidLogger(int(logLevel))

	tunDevice, _, err := tun.CreateUnmonitoredTUNFromFD(int(tunFd))
	if err != nil {
		logger.Errorf("Failed to create TUN from fd: %v", err)
		return -1
	}

	verdictDevice := newVerdictTun(tunDevice, defaultVerdictTtl)
	dev := device.NewDevice(verdictDevice, conn.NewDefaultBind(), logger)

	if err := dev.IpcSet(C.GoString(settings)); err != nil {
		logger.Errorf("Failed to apply UAPI settings: %v", err)
		dev.Close()
		return -1
	}

	if err := dev.Up(); err != nil {
		logger.Errorf("Failed to bring device up: %v", err)
		dev.Close()
		return -1
	}

	handle := nextHandle
	nextHandle++
	tunnelHandles[handle] = &tunnel{dev: dev, tun: verdictDevice}
	logger.Verbosef("Tunnel %d started", handle)
	return handle
}

//export wgTurnOff
func wgTurnOff(handle int32) {
	t, ok := tunnelHandles[handle]
	if !ok {
		return
	}
	delete(tunnelHandles, handle)
	t.dev.Close()
}

//export wgGetSocketV4
func wgGetSocketV4(handle int32) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}
	bind, ok := t.dev.Bind().(*conn.StdNetBind)
	if !ok {
		return -1
	}
	fd, err := bind.PeekLookAtSocketFd4()
	if err != nil {
		return -1
	}
	return int32(fd)
}

//export wgGetConfig
func wgGetConfig(handle int32) *C.char {
	t, ok := tunnelHandles[handle]
	if !ok {
		return nil
	}
	settings, err := t.dev.IpcGet()
	if err != nil {
		return nil
	}
	return C.CString(settings)
}

//export wgSetConfig
func wgSetConfig(handle int32, settings *C.char) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}
	if err := t.dev.IpcSet(C.GoString(settings)); err != nil {
		return -1
	}
	return 0
}

//export wgSetVerdicts
func wgSetVerdicts(handle int32, spec *C.char) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}
	t.tun.setTable(parseTable(C.GoString(spec)))
	return 0
}

//export wgPrepareSwap
func wgPrepareSwap(handle int32, on int32) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}

	t.tun.prepareSwap(on != 0)
	return 0
}

//export wgSwapTun
func wgSwapTun(handle int32, tunFd int32) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}

	next, _, err := tun.CreateUnmonitoredTUNFromFD(int(tunFd))
	if err != nil {
		return -1
	}

	t.tun.swap(next)
	return 0
}

//export wgSetVerdictTtl
func wgSetVerdictTtl(handle int32, seconds int32) int32 {
	t, ok := tunnelHandles[handle]
	if !ok || seconds < 1 {
		return -1
	}
	t.tun.setTtl(time.Duration(seconds) * time.Second)
	return 0
}

//export wgTunnelStats
func wgTunnelStats(handle int32) *C.char {
	t, ok := tunnelHandles[handle]
	if !ok {
		return nil
	}
	return C.CString(t.tun.stats())
}

//export wgSetProtector
func wgSetProtector(handle int32, fn C.ag_protect_fn) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}
	t.tun.setProtector(func(fd int) bool {
		return C.ag_call_protect(fn, C.int(fd)) != 0
	})
	return 0
}

//export wgSetRelay
func wgSetRelay(handle int32, port int32, split int32, fn C.ag_owner_fn) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}

	if port <= 0 {
		t.tun.setRelay(0, false, nil)
		return 0
	}

	t.tun.setRelay(int(port), split != 0, func(proto uint8, src uint32, srcPort uint16, dst uint32, dstPort uint16) int {
		return int(C.ag_call_owner(fn, C.int(proto), C.uint(src), C.ushort(srcPort), C.uint(dst), C.ushort(dstPort)))
	})
	return 0
}

//export wgSetTcpDirect
func wgSetTcpDirect(handle int32, enable int32) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}
	if err := t.tun.setTcpDirect(enable != 0); err != nil {
		return -1
	}
	return 0
}

//export wgLiveAddresses
func wgLiveAddresses(handle int32) *C.char {
	t, ok := tunnelHandles[handle]
	if !ok {
		return nil
	}
	return C.CString(t.tun.snapshot())
}

//export wgPreloadLive
func wgPreloadLive(handle int32, text *C.char) int32 {
	t, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}
	return int32(t.tun.preload(C.GoString(text)))
}

// c-shared requires a main function; it is never invoked.
func main() {}
