/* SPDX-License-Identifier: MIT
 *
 * Holds what the host answered about whose flow a packet belongs to (AmneziaGeo).
 */

package owners

import (
	"sync"
	"sync/atomic"
	"time"
)

// Столько ответов о владельце держим разом.
const Max = 8192

// Пауза потока, после которой о владельце спрашиваем заново.
const Hold = 3 * time.Second

// Срок, через который ответ «чужой» у идущего потока сверяется снова.
const Recheck = 30 * time.Second

// Владелец, каким его называет хост.
const (
	Other = 0
	Self  = 1
	Named = 2
)

// Поток: протокол, адреса и порты обеих сторон.
type Flow struct {
	Proto            uint8
	Src, Dst         uint32
	SrcPort, DstPort uint16
}

// Ответ хоста о владельце потока.
type held struct {
	verdict int
	// Когда спросить снова у идущего потока; ноль - не спрашивать, пока он идёт.
	again int64
	// Последний пакет потока.
	last atomic.Int64
}

// Ответы хоста по потокам; ответ держится, пока поток идёт.
type Cache struct {
	items sync.Map
	count atomic.Int64
}

// Чей это поток, как его называет хост.
func (c *Cache) Whose(flow Flow, nanos int64, ask func(uint8, uint32, uint16, uint32, uint16) int) int {
	foreign := false
	if kept, ok := c.items.Load(flow); ok {
		if answer, fits := kept.(*held); fits {
			last := answer.last.Load()
			running := nanos-last < int64(Hold)
			if running && (answer.again == 0 || nanos < answer.again) {
				if nanos-last >= int64(time.Second) {
					answer.last.Store(nanos)
				}

				return answer.verdict
			}

			foreign = running && answer.verdict == Other
		}
	}

	verdict := ask(flow.Proto, flow.Src, flow.SrcPort, flow.Dst, flow.DstPort)
	if c.count.Add(1) > Max {
		c.forget(nanos)
	}

	answer := &held{verdict: verdict}
	answer.last.Store(nanos)
	// «Чужой» сверяется снова: первый раз скоро, дальше редко.
	if verdict == Other {
		answer.again = nanos + int64(Hold)
		if foreign {
			answer.again = nanos + int64(Recheck)
		}
	}

	c.items.Store(flow, answer)
	return verdict
}

// Сколько ответов под учётом.
func (c *Cache) Size() int {
	size := 0
	c.items.Range(func(_, _ any) bool {
		size++
		return true
	})
	return size
}

// Убирает все ответы.
func (c *Cache) Clear() {
	c.items.Clear()
	c.count.Store(0)
}

// Убирает ответы потоков, которые встали.
func (c *Cache) forget(nanos int64) {
	left := int64(0)
	c.items.Range(func(key, kept any) bool {
		if answer, fits := kept.(*held); fits && nanos-answer.last.Load() < int64(Hold) {
			left++
		} else {
			c.items.Delete(key)
		}

		return true
	})
	c.count.Store(left)
}
