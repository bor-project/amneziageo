/* SPDX-License-Identifier: MIT
 *
 * Sends the datagrams a direct verdict names past the tunnel, every port of an app from one socket (AmneziaGeo).
 */

package datagram

import (
	"context"
	"encoding/binary"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

// Столько сокет живёт без пакетов.
const Idle = 120 * time.Second

// Шаг, с которым ищем простаивающие сокеты.
const sweepEvery = 30 * time.Second

// Свой адрес и порт, с которых приложение шлёт датаграммы.
type endpoint struct {
	addr uint32
	port uint16
}

// Открытый наружу сокет одного своего адреса и порта.
type flow struct {
	conn *net.UDPConn
	last atomic.Int64
	key  endpoint
}

// Куда возвращать ответ.
type Writer interface {
	Write(bufs [][]byte, offset int) (int, error)
}

// Отметка, которую сокет проходит до привязки.
type Control = func(network, address string, conn syscall.RawConn) error

// Forwarder уводит датаграммы мимо туннеля и возвращает ответы на них.
type Forwarder struct {
	tun     Writer
	mtu     int
	idle    time.Duration
	control Control

	mu     sync.RWMutex
	flows  map[endpoint]*flow
	closed bool

	Sent    atomic.Uint64
	Back    atomic.Uint64
	Dropped atomic.Uint64
	Refused atomic.Uint64

	stop chan struct{}
	once sync.Once
}

// New поднимает пересыльщик: ответы вписываются в tun, каждый сокет до отправки проходит отметку.
func New(tun Writer, mtu int, control Control) *Forwarder {
	return start(tun, mtu, control, Idle, sweepEvery)
}

// Поднимает пересыльщик с заданным сроком простоя и шагом уборки.
func start(tun Writer, mtu int, control Control, idle, every time.Duration) *Forwarder {
	f := &Forwarder{
		tun:     tun,
		mtu:     mtu,
		idle:    idle,
		control: control,
		flows:   make(map[endpoint]*flow),
		stop:    make(chan struct{}),
	}
	go f.sweep(every)
	return f
}

// Count говорит, сколько сокетов открыто наружу.
func (f *Forwarder) Count() int {
	f.mu.RLock()
	defer f.mu.RUnlock()
	return len(f.flows)
}

// Close закрывает все сокеты и больше не открывает новых.
func (f *Forwarder) Close() {
	f.once.Do(func() {
		close(f.stop)
		f.mu.Lock()
		f.closed = true
		for key, fl := range f.flows {
			fl.conn.Close()
			delete(f.flows, key)
		}
		f.mu.Unlock()
	})
}

// Send отправляет датаграмму со своего защищённого сокета; отказ возвращает пакет вызывающему.
func (f *Forwarder) Send(packet []byte) bool {
	if len(packet) < 20 {
		return false
	}
	ihl := int(packet[0]&0x0f) * 4
	if ihl < 20 || len(packet) < ihl+8 || packet[9] != syscall.IPPROTO_UDP {
		return false
	}
	key := endpoint{
		addr: binary.BigEndian.Uint32(packet[12:16]),
		port: binary.BigEndian.Uint16(packet[ihl : ihl+2]),
	}
	to := netip.AddrPortFrom(netip.AddrFrom4([4]byte(packet[16:20])), binary.BigEndian.Uint16(packet[ihl+2:ihl+4]))

	fl := f.lookup(key)
	if fl == nil {
		f.Dropped.Add(1)
		return true
	}
	if _, err := fl.conn.WriteToUDPAddrPort(packet[ihl+8:], to); err != nil {
		f.Dropped.Add(1)
		return true
	}
	f.Sent.Add(1)
	return true
}

// Сокет своего адреса и порта: один на всех адресатов.
func (f *Forwarder) lookup(key endpoint) *flow {
	now := time.Now().UnixNano()
	f.mu.RLock()
	fl, ok := f.flows[key]
	if ok {
		fl.last.Store(now)
	}
	f.mu.RUnlock()
	if ok {
		return fl
	}

	conn, err := f.open()
	if err != nil {
		f.Refused.Add(1)
		return nil
	}

	fl = &flow{conn: conn, key: key}
	fl.last.Store(now)

	f.mu.Lock()
	if existing, ok := f.flows[key]; ok || f.closed {
		f.mu.Unlock()
		conn.Close()
		return existing
	}
	f.flows[key] = fl
	f.mu.Unlock()

	go f.receive(fl)
	return fl
}

// Сокет наружу проходит отметку до первой отправки, иначе пакет уйдёт обратно в tun.
func (f *Forwarder) open() (*net.UDPConn, error) {
	config := net.ListenConfig{Control: f.control}
	conn, err := config.ListenPacket(context.Background(), "udp4", ":0")
	if err != nil {
		return nil, err
	}
	return conn.(*net.UDPConn), nil
}

// Ответы с любого адреса вписываются обратно в tun как датаграммы своему адресу и порту.
func (f *Forwarder) receive(fl *flow) {
	// Лишний байт отличает ответ по размеру tun от обрезанного.
	limit := f.mtu - 28
	buf := make([]byte, limit+1)
	packet := make([]byte, f.mtu)
	for {
		n, from, err := fl.conn.ReadFromUDPAddrPort(buf)
		if err != nil {
			f.drop(fl)
			return
		}
		fl.last.Store(time.Now().UnixNano())
		sender := from.Addr().Unmap()
		if n > limit || !sender.Is4() {
			f.Dropped.Add(1)
			continue
		}
		size := build(packet, sender.As4(), from.Port(), fl.key, buf[:n])
		if _, err := f.tun.Write([][]byte{packet[:size]}, 0); err != nil {
			f.Dropped.Add(1)
			continue
		}
		f.Back.Add(1)
	}
}

// Закрывает сокет и убирает его из таблицы, если там стоит он.
func (f *Forwarder) drop(fl *flow) {
	f.mu.Lock()
	if f.flows[fl.key] == fl {
		delete(f.flows, fl.key)
	}
	f.mu.Unlock()
	fl.conn.Close()
}

// Закрывает сокеты, по которым давно не шло пакетов ни в одну сторону.
func (f *Forwarder) sweep(every time.Duration) {
	ticker := time.NewTicker(every)
	defer ticker.Stop()
	for {
		select {
		case <-f.stop:
			return
		case <-ticker.C:
			deadline := time.Now().Add(-f.idle).UnixNano()
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

// Собирает датаграмму от отправителя ответа своему адресу и порту.
func build(out []byte, sender [4]byte, port uint16, to endpoint, payload []byte) int {
	total := 28 + len(payload)
	out[0] = 0x45
	out[1] = 0
	binary.BigEndian.PutUint16(out[2:4], uint16(total))
	binary.BigEndian.PutUint16(out[4:6], 0)
	binary.BigEndian.PutUint16(out[6:8], 0)
	out[8] = 64
	out[9] = syscall.IPPROTO_UDP
	binary.BigEndian.PutUint16(out[10:12], 0)
	copy(out[12:16], sender[:])
	binary.BigEndian.PutUint32(out[16:20], to.addr)
	binary.BigEndian.PutUint16(out[10:12], checksum(out[:20]))

	binary.BigEndian.PutUint16(out[20:22], port)
	binary.BigEndian.PutUint16(out[22:24], to.port)
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
