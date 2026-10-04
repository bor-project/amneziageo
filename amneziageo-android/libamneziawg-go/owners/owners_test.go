/* SPDX-License-Identifier: MIT
 *
 * Tests of the owner answers held by flow (AmneziaGeo).
 */

package owners

import (
	"testing"
	"time"
)

const second = int64(time.Second)

// Хост, который отвечает по адресу назначения и считает вопросы.
type host struct {
	asked   int
	answers map[uint32]int
}

func (h *host) ask(_ uint8, _ uint32, _ uint16, dst uint32, _ uint16) int {
	h.asked++
	return h.answers[dst]
}

func flow(srcPort uint16, dst uint32, dstPort uint16) Flow {
	return Flow{Proto: 6, Src: 0x0aff0002, Dst: dst, SrcPort: srcPort, DstPort: dstPort}
}

func TestTwoFlowsOnTheSamePortsAreAnsweredApart(t *testing.T) {
	cache := &Cache{}
	h := &host{answers: map[uint32]int{1: Other, 2: Self}}

	if got := cache.Whose(flow(40000, 1, 443), 0, h.ask); got != Other {
		t.Fatalf("the flow of an application was answered %d", got)
	}

	if got := cache.Whose(flow(40000, 2, 443), second, h.ask); got != Self {
		t.Fatalf("our own flow on the same ports was answered %d", got)
	}

	if got := cache.Whose(flow(40000, 1, 443), 2*second, h.ask); got != Other {
		t.Fatalf("the flow of an application was answered %d after ours", got)
	}
}

func TestAnAnswerIsHeldWhileTheFlowRuns(t *testing.T) {
	cache := &Cache{}
	h := &host{answers: map[uint32]int{1: Self}}

	for step := int64(0); step < 20; step++ {
		if got := cache.Whose(flow(40000, 1, 443), step*second, h.ask); got != Self {
			t.Fatalf("step %d answered %d", step, got)
		}
	}

	if h.asked != 1 {
		t.Fatalf("a running flow was asked about %d times", h.asked)
	}
}

func TestAFlowThatPausedIsAskedAgain(t *testing.T) {
	cache := &Cache{}
	h := &host{answers: map[uint32]int{1: Self}}

	cache.Whose(flow(40000, 1, 443), 0, h.ask)
	cache.Whose(flow(40000, 1, 443), int64(Hold)+second, h.ask)

	if h.asked != 2 {
		t.Fatalf("a flow that paused was asked about %d times", h.asked)
	}
}

func TestAForeignFlowIsCheckedSoonAndThenRarely(t *testing.T) {
	cache := &Cache{}
	h := &host{answers: map[uint32]int{1: Other}}

	for step := int64(0); step <= 40; step++ {
		cache.Whose(flow(40000, 1, 443), step*second, h.ask)
	}

	// На первом пакете, через паузу владельца и ещё раз через срок сверки.
	if h.asked != 3 {
		t.Fatalf("a foreign flow that ran for 40 s was asked about %d times", h.asked)
	}
}

func TestStoppedFlowsAreForgotten(t *testing.T) {
	cache := &Cache{}
	h := &host{answers: map[uint32]int{}}

	for i := 0; i < Max; i++ {
		cache.Whose(flow(uint16(i), uint32(i>>16)+7, 443), 0, h.ask)
	}

	if cache.Size() != Max {
		t.Fatalf("%d answers are held of %d flows", cache.Size(), Max)
	}

	cache.Whose(flow(1, 1000000, 443), int64(Hold)+second, h.ask)
	if cache.Size() != 1 {
		t.Fatalf("%d answers are held after the flows stopped", cache.Size())
	}
}

func TestClearDropsEveryAnswer(t *testing.T) {
	cache := &Cache{}
	h := &host{answers: map[uint32]int{1: Self}}

	cache.Whose(flow(40000, 1, 443), 0, h.ask)
	cache.Clear()
	cache.Whose(flow(40000, 1, 443), second, h.ask)

	if h.asked != 2 || cache.Size() != 1 {
		t.Fatalf("asked %d times, %d held", h.asked, cache.Size())
	}
}
