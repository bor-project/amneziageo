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

// Сколько запись живёт без трафика, пока хост не задал своё окно.
const defaultVerdictTtl = 300 * time.Second

// Шаг уборки: то же окно, что у релея на стороне хоста.
const (
	minSweepInterval = 5 * time.Second
	maxSweepInterval = 60 * time.Second
)

// Сколько ждать обещанный tun, прежде чем считать чтение сорванным.
const (
	swapWait = 5 * time.Second
	swapStep = 20 * time.Millisecond
)

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
	ttl   atomic.Int64
	max   int
	swept atomic.Int64
}

func newLiveSet(ttl time.Duration, max int) *liveSet {
	l := &liveSet{items: make(map[uint32]*touch), max: max}
	l.ttl.Store(int64(ttl))
	return l
}

// Меняет окно простоя на живом туннеле.
func (l *liveSet) setTtl(ttl time.Duration) {
	l.ttl.Store(int64(ttl))
}

// Текущее окно простоя.
func (l *liveSet) window() time.Duration {
	return time.Duration(l.ttl.Load())
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

// Уборка по расписанию, а не только при снятии снимка.
func (l *liveSet) sweep(nanos int64) {
	l.mu.Lock()
	l.sweepLocked(nanos)
	l.mu.Unlock()
}

func (l *liveSet) sweepLocked(nanos int64) {
	deadline := nanos - l.ttl.Load()
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
	if nanos-l.swept.Load() > l.ttl.Load() {
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
	inner   atomic.Pointer[tun.Device]
	events  chan tun.Event
	pending atomic.Bool

	mu    sync.RWMutex
	table *verdictTable

	live *liveSet
	fwd  *forwarder
	tcp  atomic.Pointer[tcpForwarder]

	protect atomic.Pointer[func(int) bool]

	stop chan struct{}
	once sync.Once

	blocked atomic.Uint64
	passed  atomic.Uint64
	seen    atomic.Uint64
}

func newVerdictTun(inner tun.Device, ttl time.Duration) *verdictTun {
	d := &verdictTun{live: newLiveSet(ttl, 65536), stop: make(chan struct{}), events: make(chan tun.Event, 8)}
	d.inner.Store(&inner)
	// Форвардеры пишут ответы через этот же слой, поэтому подмена tun доходит и до них.
	d.fwd = newForwarder(d, &d.protect)
	go d.sweeping()
	go d.relayEvents(inner)
	return d
}

// Системный tun, который слой читает прямо сейчас.
func (d *verdictTun) device() tun.Device {
	return *d.inner.Load()
}

// Объявляет подмену: до неё ошибка чтения означает закрытый хостом tun, а не мёртвый туннель.
func (d *verdictTun) prepareSwap(on bool) {
	d.pending.Store(on)
}

// Ставит под движок новый системный tun и закрывает прежний; чтение продолжается с нового.
func (d *verdictTun) swap(next tun.Device) {
	previous := d.device()
	if previous == next {
		d.pending.Store(false)
		return
	}

	d.inner.Store(&next)
	d.pending.Store(false)
	go d.relayEvents(next)
	previous.Close()
}

// Ждёт обещанный tun; по истечении ожидания чтение отдаёт свою ошибку и туннель закрывается.
func (d *verdictTun) awaitSwap(previous tun.Device) bool {
	for waited := time.Duration(0); waited < swapWait; waited += swapStep {
		if d.device() != previous {
			return true
		}

		if !d.pending.Load() {
			return false
		}

		time.Sleep(swapStep)
	}

	d.pending.Store(false)
	return false
}

// Пробрасывает события системного tun движку через собственный канал.
func (d *verdictTun) relayEvents(from tun.Device) {
	source := from.Events()
	for {
		select {
		case <-d.stop:
			return
		case event, ok := <-source:
			if !ok {
				return
			}

			select {
			case d.events <- event:
			default:
			}
		}
	}
}

// Задаёт окно простоя учёта адресов.
func (d *verdictTun) setTtl(ttl time.Duration) {
	d.live.setTtl(ttl)
}

// Отпускает простаивающие записи по расписанию.
func (d *verdictTun) sweeping() {
	for {
		interval := d.live.window() / 5
		if interval < minSweepInterval {
			interval = minSweepInterval
		}
		if interval > maxSweepInterval {
			interval = maxSweepInterval
		}

		timer := time.NewTimer(interval)
		select {
		case <-d.stop:
			timer.Stop()
			return
		case <-timer.C:
			d.live.sweep(time.Now().UnixNano())
		}
	}
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

	mtu, err := d.MTU()
	if err != nil || mtu < 576 {
		mtu = defaultTunMtu
	}

	fwd, err := newTcpForwarder(d, mtu, &d.protect)
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
	inner := d.device()
	n, err := inner.Read(bufs, sizes, offset)
	// Ошибка на подменённом tun не поднимается наверх: движок закрывается на любой другой.
	for err != nil && (d.device() != inner || d.pending.Load()) {
		if d.device() == inner && !d.awaitSwap(inner) {
			break
		}

		inner = d.device()
		n, err = inner.Read(bufs, sizes, offset)
	}

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
	return d.device().Write(bufs, offset)
}

func (d *verdictTun) File() *os.File           { return d.device().File() }
func (d *verdictTun) MTU() (int, error)        { return d.device().MTU() }
func (d *verdictTun) Name() (string, error)    { return d.device().Name() }
func (d *verdictTun) Events() <-chan tun.Event { return d.events }
func (d *verdictTun) Close() error {
	d.once.Do(func() { close(d.stop) })
	if fwd := d.tcp.Swap(nil); fwd != nil {
		fwd.close()
	}

	d.fwd.close()
	return d.device().Close()
}
func (d *verdictTun) BatchSize() int { return d.device().BatchSize() }

// Адрес назначения пакета IPv4; остальному трафику вердикт не ставится.
func destinationIPv4(packet []byte) (uint32, bool) {
	if len(packet) < 20 || packet[0]>>4 != 4 {
		return 0, false
	}
	return binary.BigEndian.Uint32(packet[16:20]), true
}
