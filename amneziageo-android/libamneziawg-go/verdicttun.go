//go:build android

/* SPDX-License-Identifier: MIT
 *
 * Verdict layer between the system tun and the engine (AmneziaGeo).
 */

package main

import (
	"encoding/binary"
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
)

// Сколько запись живёт без трафика.
const defaultVerdictTtl = 300 * time.Second

type verdict uint8

const (
	verdictProxy verdict = iota
	verdictDirect
	verdictBlock
)

func (v verdict) String() string {
	switch v {
	case verdictDirect:
		return "direct"
	case verdictBlock:
		return "block"
	}
	return "proxy"
}

// Границы диапазона адресов.
type span struct {
	lo, hi uint32
}

// Диапазоны одной роли, слитые и отсортированные.
type spanSet struct {
	spans []span
}

func newSpanSet(spans []span) *spanSet {
	sort.Slice(spans, func(i, j int) bool {
		if spans[i].lo != spans[j].lo {
			return spans[i].lo < spans[j].lo
		}
		return spans[i].hi < spans[j].hi
	})
	merged := spans[:0]
	for _, s := range spans {
		if n := len(merged); n > 0 && s.lo <= merged[n-1].hi+1 && merged[n-1].hi != ^uint32(0) {
			if s.hi > merged[n-1].hi {
				merged[n-1].hi = s.hi
			}
			continue
		}
		merged = append(merged, s)
	}
	return &spanSet{spans: merged}
}

func (s *spanSet) has(addr uint32) bool {
	if s == nil || len(s.spans) == 0 {
		return false
	}
	i := sort.Search(len(s.spans), func(i int) bool { return s.spans[i].hi >= addr })
	return i < len(s.spans) && s.spans[i].lo <= addr
}

// Роли в порядке старшинства: блок сильнее директа, директ сильнее прокси.
type verdictTable struct {
	block  *spanSet
	direct *spanSet
	proxy  *spanSet
}

func (t *verdictTable) lookup(addr uint32) (verdict, bool) {
	if t == nil {
		return verdictProxy, false
	}
	if t.block.has(addr) {
		return verdictBlock, true
	}
	if t.direct.has(addr) {
		return verdictDirect, true
	}
	if t.proxy.has(addr) {
		return verdictProxy, true
	}
	return verdictProxy, false
}

// Разбирает строки «cidr=роль», по одной на строку.
func parseTable(spec string) *verdictTable {
	var block, direct, proxy []span
	for _, line := range strings.Split(spec, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		eq := strings.LastIndexByte(line, '=')
		if eq < 0 {
			continue
		}
		lo, hi, ok := parseCidr(line[:eq])
		if !ok {
			continue
		}
		switch line[eq+1:] {
		case "block":
			block = append(block, span{lo, hi})
		case "direct":
			direct = append(direct, span{lo, hi})
		case "proxy":
			proxy = append(proxy, span{lo, hi})
		}
	}
	return &verdictTable{
		block:  newSpanSet(block),
		direct: newSpanSet(direct),
		proxy:  newSpanSet(proxy),
	}
}

func parseCidr(text string) (uint32, uint32, bool) {
	slash := strings.IndexByte(text, '/')
	if slash < 0 {
		return 0, 0, false
	}
	addr, ok := parseIPv4(text[:slash])
	if !ok {
		return 0, 0, false
	}
	bits, err := strconv.Atoi(text[slash+1:])
	if err != nil || bits < 0 || bits > 32 {
		return 0, 0, false
	}
	if bits == 0 {
		return 0, ^uint32(0), true
	}
	mask := ^uint32(0) << (32 - bits)
	return addr & mask, (addr & mask) | ^mask, true
}

func parseIPv4(text string) (uint32, bool) {
	parts := strings.Split(text, ".")
	if len(parts) != 4 {
		return 0, false
	}
	var out uint32
	for _, part := range parts {
		n, err := strconv.Atoi(part)
		if err != nil || n < 0 || n > 255 {
			return 0, false
		}
		out = out<<8 | uint32(n)
	}
	return out, true
}

func formatIPv4(addr uint32) string {
	var b [4]byte
	binary.BigEndian.PutUint32(b[:], addr)
	return strconv.Itoa(int(b[0])) + "." + strconv.Itoa(int(b[1])) + "." +
		strconv.Itoa(int(b[2])) + "." + strconv.Itoa(int(b[3]))
}

// Адрес, которого коснулся трафик.
type touch struct {
	last atomic.Int64
	v    verdict
}

// Что реально используется; запись живёт, пока по ней идёт трафик.
type liveSet struct {
	mu    sync.RWMutex
	items map[uint32]*touch
	ttl   time.Duration
	max   int
	swept atomic.Int64
}

func newLiveSet(ttl time.Duration, max int) *liveSet {
	return &liveSet{items: make(map[uint32]*touch), ttl: ttl, max: max}
}

// Отметка идёт по каждому пакету, поэтому запись обновляется не чаще раза в секунду.
func (l *liveSet) note(addr uint32, v verdict, nanos int64) {
	l.mu.RLock()
	item, ok := l.items[addr]
	l.mu.RUnlock()
	if ok {
		if nanos-item.last.Load() >= int64(time.Second) {
			item.last.Store(nanos)
		}
		return
	}

	l.mu.Lock()
	if item, ok = l.items[addr]; !ok {
		if len(l.items) >= l.max {
			l.sweepLocked(nanos)
		}
		item = &touch{v: v}
		item.last.Store(nanos)
		l.items[addr] = item
	}
	l.mu.Unlock()
}

func (l *liveSet) sweepLocked(nanos int64) {
	deadline := nanos - int64(l.ttl)
	for addr, item := range l.items {
		if item.last.Load() < deadline {
			delete(l.items, addr)
		}
	}
	l.swept.Store(nanos)
}

// Сколько адресов под учётом.
func (l *liveSet) size() int {
	l.mu.RLock()
	defer l.mu.RUnlock()
	return len(l.items)
}

// Строки «адрес роль возраст-в-секундах» для стороны хоста.
func (l *liveSet) snapshot(nanos int64) string {
	l.mu.Lock()
	defer l.mu.Unlock()
	if nanos-l.swept.Load() > int64(l.ttl) {
		l.sweepLocked(nanos)
	}
	var out strings.Builder
	for addr, item := range l.items {
		out.WriteString(formatIPv4(addr))
		out.WriteByte(' ')
		out.WriteString(item.v.String())
		out.WriteByte(' ')
		out.WriteString(strconv.FormatInt((nanos-item.last.Load())/int64(time.Second), 10))
		out.WriteByte('\n')
	}
	return out.String()
}

// Стоит между системным tun и движком: ведёт учёт живых адресов и снимает блокированные пакеты.
type verdictTun struct {
	inner tun.Device

	mu    sync.RWMutex
	table *verdictTable

	live *liveSet
	fwd  *forwarder
	tcp  atomic.Pointer[tcpForwarder]

	protect atomic.Pointer[func(int) bool]

	blocked atomic.Uint64
	passed  atomic.Uint64
	seen    atomic.Uint64
}

func newVerdictTun(inner tun.Device, ttl time.Duration) *verdictTun {
	d := &verdictTun{inner: inner, live: newLiveSet(ttl, 65536)}
	d.fwd = newForwarder(inner, &d.protect)
	return d
}

// Кто отпускает наш сокет мимо туннеля.
func (d *verdictTun) setProtector(fn func(int) bool) {
	d.protect.Store(&fn)
}

// Поднимает или гасит свой стек под потоки мимо туннеля.
func (d *verdictTun) setTcpDirect(on bool) error {
	if !on {
		if fwd := d.tcp.Swap(nil); fwd != nil {
			fwd.close()
		}
		return nil
	}

	if d.tcp.Load() != nil {
		return nil
	}

	mtu, err := d.inner.MTU()
	if err != nil || mtu < 576 {
		mtu = defaultTunMtu
	}

	fwd, err := newTcpForwarder(d.inner, mtu, &d.protect)
	if err != nil {
		return err
	}

	d.tcp.Store(fwd)
	return nil
}

// Уводит пакет мимо туннеля своим сокетом; отказ оставляет его туннелю.
func (d *verdictTun) aside(packet []byte) bool {
	if len(packet) >= 20 && packet[9] == syscall.IPPROTO_TCP {
		if fwd := d.tcp.Load(); fwd != nil {
			return fwd.send(packet)
		}

		return false
	}

	return d.fwd.send(packet)
}

// Счётчики слоя вердиктов, форвардера и стека потоков.
func (d *verdictTun) stats() string {
	streams := "streams off"
	if fwd := d.tcp.Load(); fwd != nil {
		streams = fwd.stats()
	}

	return fmt.Sprintf(
		"named %d, blocked %d, direct %d, sent %d, answered %d, dropped %d, refused %d, %d flow(s), %d live; %s",
		d.seen.Load(), d.blocked.Load(), d.passed.Load(),
		d.fwd.sent.Load(), d.fwd.back.Load(), d.fwd.dropped.Load(), d.fwd.refused.Load(),
		d.fwd.count(), d.live.size(), streams)
}

// Что реально используется прямо сейчас.
func (d *verdictTun) snapshot() string {
	return d.live.snapshot(time.Now().UnixNano())
}

func (d *verdictTun) setTable(table *verdictTable) {
	d.mu.Lock()
	d.table = table
	d.mu.Unlock()
}

func (d *verdictTun) verdictFor(addr uint32) (verdict, bool) {
	d.mu.RLock()
	table := d.table
	d.mu.RUnlock()
	return table.lookup(addr)
}

func (d *verdictTun) Read(bufs [][]byte, sizes []int, offset int) (int, error) {
	n, err := d.inner.Read(bufs, sizes, offset)
	if n == 0 || err != nil {
		return n, err
	}
	nanos := time.Now().UnixNano()
	kept := 0
	for i := 0; i < n; i++ {
		if addr, ok := destinationIPv4(bufs[i][offset : offset+sizes[i]]); ok {
			v, named := d.verdictFor(addr)
			d.seen.Add(1)
			if named {
				d.live.note(addr, v, nanos)
			}
			switch v {
			case verdictBlock:
				d.blocked.Add(1)
				continue
			case verdictDirect:
				// Датаграмма и поток уходят со своих защищённых сокетов; остальное едет туннелем.
				if d.aside(bufs[i][offset : offset+sizes[i]]) {
					d.passed.Add(1)
					continue
				}
			}
		}
		if kept != i {
			bufs[kept], bufs[i] = bufs[i], bufs[kept]
			sizes[kept] = sizes[i]
		}
		kept++
	}
	return kept, nil
}

func (d *verdictTun) Write(bufs [][]byte, offset int) (int, error) {
	return d.inner.Write(bufs, offset)
}

func (d *verdictTun) File() *os.File           { return d.inner.File() }
func (d *verdictTun) MTU() (int, error)        { return d.inner.MTU() }
func (d *verdictTun) Name() (string, error)    { return d.inner.Name() }
func (d *verdictTun) Events() <-chan tun.Event { return d.inner.Events() }
func (d *verdictTun) Close() error {
	if fwd := d.tcp.Swap(nil); fwd != nil {
		fwd.close()
	}

	d.fwd.close()
	return d.inner.Close()
}
func (d *verdictTun) BatchSize() int { return d.inner.BatchSize() }

// Адрес назначения пакета IPv4; остальному трафику вердикт не ставится.
func destinationIPv4(packet []byte) (uint32, bool) {
	if len(packet) < 20 || packet[0]>>4 != 4 {
		return 0, false
	}
	return binary.BigEndian.Uint32(packet[16:20]), true
}
