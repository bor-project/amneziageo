//go:build android

/* SPDX-License-Identifier: MIT
 *
 * Sends the datagrams a direct verdict names past the tunnel (AmneziaGeo).
 */

package main

import (
	"encoding/binary"
	"net"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

// Столько поток живёт без пакетов.
const flowIdle = 120 * time.Second

// Размер tun, когда его не удалось спросить.
const defaultTunMtu = 1420

// Поток «свой адрес и порт - чужой адрес и порт».
type flowKey struct {
	src, dst     uint32
	sport, dport uint16
}

// Открытый наружу сокет одного потока.
type flow struct {
	conn *net.UDPConn
	last atomic.Int64
	key  flowKey
}

// Кому отдавать пакеты с вердиктом «мимо туннеля».
type forwarder struct {
	tun tun2Writer
	mtu int

	mu    sync.RWMutex
	flows map[flowKey]*flow

	protect *atomic.Pointer[func(int) bool]

	sent    atomic.Uint64
	back    atomic.Uint64
	dropped atomic.Uint64
	refused atomic.Uint64

	stop chan struct{}
	once sync.Once
}

// Куда возвращать ответ: это тот же системный tun, что читает движок.
type tun2Writer interface {
	Write(bufs [][]byte, offset int) (int, error)
	MTU() (int, error)
}

func newForwarder(w tun2Writer, protect *atomic.Pointer[func(int) bool]) *forwarder {
	mtu, err := w.MTU()
	if err != nil || mtu < 576 {
		mtu = defaultTunMtu
	}
	f := &forwarder{tun: w, mtu: mtu, flows: make(map[flowKey]*flow), protect: protect, stop: make(chan struct{})}
	go f.sweep()
	return f
}

// Сколько потоков открыто наружу.
func (f *forwarder) count() int {
	f.mu.RLock()
	defer f.mu.RUnlock()
	return len(f.flows)
}

func (f *forwarder) close() {
	f.once.Do(func() {
		close(f.stop)
		f.mu.Lock()
		for key, fl := range f.flows {
			fl.conn.Close()
			delete(f.flows, key)
		}
		f.mu.Unlock()
	})
}

// Отправляет датаграмму со своего защищённого сокета; отказ возвращает пакет вызывающему.
func (f *forwarder) send(packet []byte) bool {
	if len(packet) < 20 {
		return false
	}
	ihl := int(packet[0]&0x0f) * 4
	if ihl < 20 || len(packet) < ihl+8 || packet[9] != syscall.IPPROTO_UDP {
		return false
	}
	key := flowKey{
		src:   binary.BigEndian.Uint32(packet[12:16]),
		dst:   binary.BigEndian.Uint32(packet[16:20]),
		sport: binary.BigEndian.Uint16(packet[ihl : ihl+2]),
		dport: binary.BigEndian.Uint16(packet[ihl+2 : ihl+4]),
	}
	payload := packet[ihl+8:]

	fl := f.lookup(key)
	if fl == nil {
		f.dropped.Add(1)
		return true
	}
	fl.last.Store(time.Now().UnixNano())
	if _, err := fl.conn.Write(payload); err != nil {
		f.drop(key)
		f.dropped.Add(1)
		return true
	}
	f.sent.Add(1)
	return true
}

func (f *forwarder) lookup(key flowKey) *flow {
	f.mu.RLock()
	fl, ok := f.flows[key]
	f.mu.RUnlock()
	if ok {
		return fl
	}

	conn, err := f.dial(key)
	if err != nil {
		f.refused.Add(1)
		return nil
	}

	fl = &flow{conn: conn, key: key}
	fl.last.Store(time.Now().UnixNano())

	f.mu.Lock()
	if existing, ok := f.flows[key]; ok {
		f.mu.Unlock()
		conn.Close()
		return existing
	}
	f.flows[key] = fl
	f.mu.Unlock()

	go f.receive(fl)
	return fl
}

// Сокет наружу защищается от туннеля до соединения, иначе пакет уйдёт обратно в tun.
func (f *forwarder) dial(key flowKey) (*net.UDPConn, error) {
	dialer := net.Dialer{Control: protectControl(f.protect)}
	addr := &net.UDPAddr{IP: net.IPv4(byte(key.dst>>24), byte(key.dst>>16), byte(key.dst>>8), byte(key.dst)), Port: int(key.dport)}
	conn, err := dialer.Dial("udp4", addr.String())
	if err != nil {
		return nil, err
	}
	return conn.(*net.UDPConn), nil
}

// Ответы приходят на тот же сокет и вписываются обратно в tun как обычная датаграмма.
func (f *forwarder) receive(fl *flow) {
	// Лишний байт отличает ответ по размеру tun от обрезанного.
	limit := f.mtu - 28
	buf := make([]byte, limit+1)
	packet := make([]byte, f.mtu)
	for {
		select {
		case <-f.stop:
			return
		default:
		}
		fl.conn.SetReadDeadline(time.Now().Add(flowIdle))
		n, err := fl.conn.Read(buf)
		if err != nil {
			f.drop(fl.key)
			return
		}
		fl.last.Store(time.Now().UnixNano())
		if n > limit {
			f.dropped.Add(1)
			continue
		}
		size := buildDatagram(packet, fl.key, buf[:n])
		bufs := [][]byte{packet[:size]}
		if _, err := f.tun.Write(bufs, 0); err != nil {
			f.drop(fl.key)
			return
		}
		f.back.Add(1)
	}
}

func (f *forwarder) drop(key flowKey) {
	f.mu.Lock()
	fl, ok := f.flows[key]
	if ok {
		delete(f.flows, key)
	}
	f.mu.Unlock()
	if ok {
		fl.conn.Close()
	}
}

func (f *forwarder) sweep() {
	ticker := time.NewTicker(30 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-f.stop:
			return
		case <-ticker.C:
			deadline := time.Now().Add(-flowIdle).UnixNano()
			f.mu.Lock()
			for key, fl := range f.flows {
				if fl.last.Load() < deadline {
					fl.conn.Close()
					delete(f.flows, key)
				}
			}
			f.mu.Unlock()
		}
	}
}

// Собирает ответный пакет: адреса и порты меняются местами относительно исходного потока.
func buildDatagram(out []byte, key flowKey, payload []byte) int {
	total := 28 + len(payload)
	out[0] = 0x45
	out[1] = 0
	binary.BigEndian.PutUint16(out[2:4], uint16(total))
	binary.BigEndian.PutUint16(out[4:6], 0)
	binary.BigEndian.PutUint16(out[6:8], 0)
	out[8] = 64
	out[9] = syscall.IPPROTO_UDP
	binary.BigEndian.PutUint16(out[10:12], 0)
	binary.BigEndian.PutUint32(out[12:16], key.dst)
	binary.BigEndian.PutUint32(out[16:20], key.src)
	binary.BigEndian.PutUint16(out[10:12], checksum(out[:20]))

	binary.BigEndian.PutUint16(out[20:22], key.dport)
	binary.BigEndian.PutUint16(out[22:24], key.sport)
	binary.BigEndian.PutUint16(out[24:26], uint16(8+len(payload)))
	binary.BigEndian.PutUint16(out[26:28], 0)
	copy(out[28:], payload)
	binary.BigEndian.PutUint16(out[26:28], udpChecksum(out[:total]))
	return total
}

func checksum(header []byte) uint16 {
	var sum uint32
	for i := 0; i+1 < len(header); i += 2 {
		sum += uint32(binary.BigEndian.Uint16(header[i : i+2]))
	}
	for sum>>16 != 0 {
		sum = (sum & 0xffff) + (sum >> 16)
	}
	return ^uint16(sum)
}

// Контрольная сумма датаграммы считается вместе с псевдозаголовком.
func udpChecksum(packet []byte) uint16 {
	udp := packet[20:]
	var sum uint32
	sum += uint32(binary.BigEndian.Uint16(packet[12:14]))
	sum += uint32(binary.BigEndian.Uint16(packet[14:16]))
	sum += uint32(binary.BigEndian.Uint16(packet[16:18]))
	sum += uint32(binary.BigEndian.Uint16(packet[18:20]))
	sum += uint32(syscall.IPPROTO_UDP)
	sum += uint32(len(udp))
	for i := 0; i+1 < len(udp); i += 2 {
		sum += uint32(binary.BigEndian.Uint16(udp[i : i+2]))
	}
	if len(udp)%2 == 1 {
		sum += uint32(udp[len(udp)-1]) << 8
	}
	for sum>>16 != 0 {
		sum = (sum & 0xffff) + (sum >> 16)
	}
	out := ^uint16(sum)
	if out == 0 {
		return 0xffff
	}
	return out
}
