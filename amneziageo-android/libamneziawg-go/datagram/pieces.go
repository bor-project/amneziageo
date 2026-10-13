/* SPDX-License-Identifier: MIT
 *
 * Puts together the datagrams the system cut to the size of the tun and remembers where each of them went (AmneziaGeo).
 */

package datagram

import (
	"encoding/binary"
	"sync"
	"time"
)

// Столько разрезанных датаграмм держим разом.
const maxCut = 16

// Срок, за который куски одной датаграммы приходят все.
const cutWait = 5 * time.Second

// Самая длинная датаграмма вместе с заголовком UDP.
const maxDatagram = 65535 - 20

// Признак «за этим куском идут ещё» и маска смещения куска в заголовке IPv4.
const (
	moreFlag   = 0x2000
	offsetMask = 0x1fff
)

// Разрезанная датаграмма: отправитель, адресат и её номер.
type cut struct {
	src, dst uint32
	id       uint16
}

// Занятый участок датаграммы.
type filled struct {
	lo, hi int
}

// Куски одной датаграммы, пришедшие к этому часу.
type gathered struct {
	body  []byte
	spans []filled
	got   int
	total int
	born  int64
}

// Собирает датаграммы из кусков.
type gatherer struct {
	mu    sync.Mutex
	wait  time.Duration
	items map[cut]*gathered
}

// Cut говорит, что пакет - кусок разрезанной датаграммы.
func Cut(packet []byte) bool {
	return len(packet) >= 20 && binary.BigEndian.Uint16(packet[6:8])&(moreFlag|offsetMask) != 0
}

// Later говорит, что пакет - продолжение разрезанной датаграммы: портов в нём нет.
func Later(packet []byte) bool {
	return len(packet) >= 20 && binary.BigEndian.Uint16(packet[6:8])&offsetMask != 0
}

// Чей это кусок.
func cutOf(packet []byte) cut {
	return cut{
		src: binary.BigEndian.Uint32(packet[12:16]),
		dst: binary.BigEndian.Uint32(packet[16:20]),
		id:  binary.BigEndian.Uint16(packet[4:6]),
	}
}

// Кладёт кусок и отдаёт датаграмму, когда пришли все; второе число - сколько датаграмм брошено несобранными.
func (g *gatherer) add(packet []byte, ihl int, nanos int64) ([]byte, int) {
	key := cutOf(packet)
	field := binary.BigEndian.Uint16(packet[6:8])
	offset := int(field&offsetMask) * 8
	data := packet[ihl:]
	last := field&moreFlag == 0

	g.mu.Lock()
	defer g.mu.Unlock()
	lost := g.expireLocked(nanos)
	if len(data) == 0 || offset+len(data) > maxDatagram || (!last && len(data)%8 != 0) {
		return nil, lost + g.forgetLocked(key)
	}

	item, ok := g.items[key]
	if !ok {
		if len(g.items) >= maxCut {
			lost += g.evictLocked()
		}

		if g.items == nil {
			g.items = make(map[cut]*gathered)
		}

		item = &gathered{total: -1, born: nanos}
		g.items[key] = item
	}

	end := offset + len(data)
	for _, span := range item.spans {
		if offset < span.hi && span.lo < end {
			return nil, lost
		}

		if last && span.hi > end {
			delete(g.items, key)
			return nil, lost + 1
		}
	}

	if item.total >= 0 && (last || end > item.total) {
		delete(g.items, key)
		return nil, lost + 1
	}

	if last {
		item.total = end
	}

	if end > len(item.body) {
		item.body = append(item.body, make([]byte, end-len(item.body))...)
	}

	copy(item.body[offset:], data)
	item.spans = append(item.spans, filled{offset, end})
	item.got += len(data)
	if item.total < 0 || item.got < item.total {
		return nil, lost
	}

	delete(g.items, key)
	return item.body, lost
}

// Бросает датаграммы, куски которых не пришли за срок.
func (g *gatherer) expire(nanos int64) int {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.expireLocked(nanos)
}

func (g *gatherer) expireLocked(nanos int64) int {
	lost := 0
	for key, item := range g.items {
		if nanos-item.born > int64(g.wait) {
			delete(g.items, key)
			lost++
		}
	}
	return lost
}

// Бросает датаграмму, чей кусок не годится.
func (g *gatherer) forgetLocked(key cut) int {
	if _, ok := g.items[key]; !ok {
		return 0
	}

	delete(g.items, key)
	return 1
}

// Бросает самую старую из собираемых.
func (g *gatherer) evictLocked() int {
	var oldest cut
	born := int64(-1)
	for key, item := range g.items {
		if born < 0 || item.born < born {
			oldest, born = key, item.born
		}
	}

	if born < 0 {
		return 0
	}

	delete(g.items, oldest)
	return 1
}

// Куда ушло начало разрезанной датаграммы.
type went struct {
	aside bool
	at    int64
}

// Heads помнит, куда ушло начало каждой разрезанной датаграммы: её продолжения идут туда же.
type Heads struct {
	mu    sync.Mutex
	items map[cut]went
}

// Note запоминает, куда ушло начало разрезанной датаграммы.
func (h *Heads) Note(packet []byte, aside bool, nanos int64) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.items) >= maxCut {
		for key, item := range h.items {
			if nanos-item.at > int64(cutWait) {
				delete(h.items, key)
			}
		}
	}

	if len(h.items) >= maxCut {
		clear(h.items)
	}

	if h.items == nil {
		h.items = make(map[cut]went)
	}

	h.items[cutOf(packet)] = went{aside: aside, at: nanos}
}

// Went говорит, куда ушло начало датаграммы, чьё продолжение пришло.
func (h *Heads) Went(packet []byte, nanos int64) (aside, known bool) {
	h.mu.Lock()
	defer h.mu.Unlock()
	item, ok := h.items[cutOf(packet)]
	if !ok || nanos-item.at > int64(cutWait) {
		return false, false
	}
	return item.aside, true
}
