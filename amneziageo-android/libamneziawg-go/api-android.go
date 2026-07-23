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
*/
import "C"

import (
	"fmt"
	"unsafe"

	"github.com/amnezia-vpn/amneziawg-go/conn"
	"github.com/amnezia-vpn/amneziawg-go/device"
	"github.com/amnezia-vpn/amneziawg-go/tun"
)

const logTag = "amneziawg-go"

var (
	tunnelHandles = make(map[int32]*device.Device)
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
func wgTurnOn(settings *C.char, tunFd int32) int32 {
	logger := newAndroidLogger(device.LogLevelVerbose)

	tunDevice, _, err := tun.CreateUnmonitoredTUNFromFD(int(tunFd))
	if err != nil {
		logger.Errorf("Failed to create TUN from fd: %v", err)
		return -1
	}

	dev := device.NewDevice(tunDevice, conn.NewDefaultBind(), logger)

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
	tunnelHandles[handle] = dev
	logger.Verbosef("Tunnel %d started", handle)
	return handle
}

//export wgTurnOff
func wgTurnOff(handle int32) {
	dev, ok := tunnelHandles[handle]
	if !ok {
		return
	}
	delete(tunnelHandles, handle)
	dev.Close()
}

//export wgGetSocketV4
func wgGetSocketV4(handle int32) int32 {
	dev, ok := tunnelHandles[handle]
	if !ok {
		return -1
	}
	bind, ok := dev.Bind().(*conn.StdNetBind)
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
	dev, ok := tunnelHandles[handle]
	if !ok {
		return nil
	}
	settings, err := dev.IpcGet()
	if err != nil {
		return nil
	}
	return C.CString(settings)
}

// c-shared requires a main function; it is never invoked.
func main() {}
