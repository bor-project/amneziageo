/* SPDX-License-Identifier: MIT
 *
 * Handshake probe on an engine that carries nothing (AmneziaGeo).
 */

package probe

import (
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
)

// Исходы пробы.
const (
	Answered = 1
	Silent   = 0
	Failed   = -1
)

// Размер пакета пустого tun.
const idleMtu = 1280

// Шаг опроса рукопожатия.
const pollStep = 100 * time.Millisecond

// Пустой tun: из него ничего не приходит, записанное в него пропадает.
type idleTun struct {
	events chan tun.Event
	closed chan struct{}
	once   sync.Once
}

func newIdleTun() *idleTun {
	return &idleTun{events: make(chan tun.Event), closed: make(chan struct{})}
}

func (t *idleTun) File() *os.File { return nil }

func (t *idleTun) Read(bufs [][]byte, sizes []int, offset int) (int, error) {
	<-t.closed
	return 0, os.ErrClosed
}

func (t *idleTun) Write(bufs [][]byte, offset int) (int, error) { return len(bufs), nil }

func (t *idleTun) MTU() (int, error) { return idleMtu, nil }

func (t *idleTun) Name() (string, error) { return "probe", nil }

func (t *idleTun) Events() <-chan tun.Event { return t.events }

func (t *idleTun) Close() error {
	t.once.Do(func() {
		close(t.closed)
		close(t.events)
	})
	return nil
}

func (t *idleTun) BatchSize() int { return 1 }

// Split делит настройки на часть устройства и часть пиров; порт устройства отбрасывается.
func Split(settings string) (own string, peers string) {
	var device, rest strings.Builder
	told := false
	for _, line := range strings.Split(settings, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		if strings.HasPrefix(line, "public_key=") {
			told = true
		}
		if told {
			rest.WriteString(line + "\n")
		} else if !strings.HasPrefix(line, "listen_port=") {
			device.WriteString(line + "\n")
		}
	}
	return device.String(), rest.String()
}

// Run поднимает движок на пустом tun и ждёт рукопожатия пира; opened зовётся до того, как пир назван.
func Run(settings string, opened func(*device.Device), wait time.Duration, logger *device.Logger) int {
	own, peers := Split(settings)
	if peers == "" {
		return Failed
	}

	dev := device.NewDevice(newIdleTun(), conn.NewDefaultBind(), logger)
	defer dev.Close()

	if err := dev.IpcSet(own); err != nil {
		logger.Errorf("Probe: failed to apply the device settings: %v", err)
		return Failed
	}
	if err := dev.Up(); err != nil {
		logger.Errorf("Probe: failed to bring the device up: %v", err)
		return Failed
	}
	if opened != nil {
		opened(dev)
	}
	if err := dev.IpcSet(peers); err != nil {
		logger.Errorf("Probe: failed to apply the peer settings: %v", err)
		return Failed
	}

	deadline := time.Now().Add(wait)
	for {
		if shaken(dev) {
			return Answered
		}
		if !time.Now().Before(deadline) {
			return Silent
		}
		time.Sleep(pollStep)
	}
}

// Ответил ли пир рукопожатием.
func shaken(dev *device.Device) bool {
	settings, err := dev.IpcGet()
	if err != nil {
		return false
	}
	for _, line := range strings.Split(settings, "\n") {
		value, found := strings.CutPrefix(line, "last_handshake_time_sec=")
		if !found {
			continue
		}
		if seconds, err := strconv.ParseInt(value, 10, 64); err == nil && seconds > 0 {
			return true
		}
	}
	return false
}
