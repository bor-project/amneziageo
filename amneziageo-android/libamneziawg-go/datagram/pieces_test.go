/* SPDX-License-Identifier: MIT
 *
 * Tests of the datagrams longer than the tun, in both directions (AmneziaGeo).
 */

package datagram

import (
	"encoding/binary"
	"net/netip"
	"strings"
	"testing"
	"time"
)

// Датаграмма приложения в три тысячи байт.
var big = strings.Repeat("abcdefgh", 375)

// Режет датаграмму приложения на куски, как система режет её под размер tun.
func pieces(id, port uint16, to netip.AddrPort, payload string, room int) [][]byte {
	whole := outgoing(port, to, payload)
	body := whole[20:]
	var out [][]byte
	for offset := 0; offset < len(body); offset += room {
		size := min(room, len(body)-offset)
		field := uint16(offset / 8)
		if offset+size < len(body) {
			field |= moreFlag
		}

		packet := make([]byte, 20+size)
		copy(packet, whole[:20])
		binary.BigEndian.PutUint16(packet[2:4], uint16(len(packet)))
		binary.BigEndian.PutUint16(packet[4:6], id)
		binary.BigEndian.PutUint16(packet[6:8], field)
		copy(packet[20:], body[offset:offset+size])
		out = append(out, packet)
	}
	return out
}

// Куски одного ответа, вписанные в tun: каждый по размеру tun, со своим местом и общим номером.
func cutAnswer(t *testing.T, tun *sink, mtu, count int) ([]byte, uint16) {
	t.Helper()
	var whole []byte
	id := uint16(0)
	for index := 0; index < count; index++ {
		var packet []byte
		select {
		case packet = <-tun.packets:
		case <-time.After(3 * time.Second):
			t.Fatalf("piece %d of %d was not written to the tun", index+1, count)
		}

		if len(packet) < 20 || len(packet) > mtu || folded(packet[:20], 0) != 0xffff {
			t.Fatalf("piece %d has %d byte(s) for a tun of %d, or an unsound header", index+1, len(packet), mtu)
		}

		if total := int(binary.BigEndian.Uint16(packet[2:4])); total != len(packet) {
			t.Fatalf("piece %d says %d byte(s) and has %d", index+1, total, len(packet))
		}

		field := binary.BigEndian.Uint16(packet[6:8])
		if index == 0 {
			whole = append(whole, packet[:20]...)
			id = binary.BigEndian.Uint16(packet[4:6])
		}

		if at := int(field&offsetMask) * 8; at != len(whole)-20 {
			t.Fatalf("piece %d stands at %d, and %d byte(s) came before it", index+1, at, len(whole)-20)
		}

		if more := field&moreFlag != 0; more != (index < count-1) {
			t.Fatalf("piece %d of %d says more follow: %v", index+1, count, more)
		}

		if number := binary.BigEndian.Uint16(packet[4:6]); number != id {
			t.Fatalf("piece %d carries the number %d, the first one %d", index+1, number, id)
		}

		whole = append(whole, packet[20:]...)
	}
	return whole, id
}

// Ответ, который пересыльщик вписал в tun кусками.
func joined(t *testing.T, tun *sink, mtu, count int) answer {
	t.Helper()
	whole, _ := cutAnswer(t, tun, mtu, count)
	binary.BigEndian.PutUint16(whole[2:4], uint16(len(whole)))
	binary.BigEndian.PutUint16(whole[6:8], 0)
	binary.BigEndian.PutUint16(whole[10:12], 0)
	binary.BigEndian.PutUint16(whole[10:12], ^folded(whole[:20], 0))
	return parsed(t, whole)
}

// Номер, под которым разрезан ответ.
func numbered(t *testing.T, tun *sink, count int) uint16 {
	t.Helper()
	_, id := cutAnswer(t, tun, 576, count)
	return id
}

func TestADatagramTheSystemCutLeavesWhole(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)

	for _, piece := range pieces(7, 40000, at(far), big, 1400) {
		if !f.Send(piece) {
			t.Fatal("a piece of a datagram was returned to the tunnel")
		}
	}

	if said, _ := heard(t, far); said != big {
		t.Fatalf("the addressee got %d byte(s) of %d", len(said), len(big))
	}

	if !deaf(far, 200*time.Millisecond) {
		t.Fatal("more than one datagram left for one the system cut")
	}

	if sent, dropped, count := f.Sent.Load(), f.Dropped.Load(), f.Count(); sent != 1 || dropped != 0 || count != 1 {
		t.Fatalf("sent %d, dropped %d, %d socket(s) open", sent, dropped, count)
	}
}

func TestThePiecesOfADatagramComeInAnyOrder(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)
	parts := pieces(7, 40000, at(far), big, 1400)

	f.Send(parts[2])
	f.Send(parts[0])
	f.Send(parts[0])
	f.Send(parts[1])

	if said, _ := heard(t, far); said != big {
		t.Fatalf("the addressee got %d byte(s) of %d", len(said), len(big))
	}

	if sent, dropped := f.Sent.Load(), f.Dropped.Load(); sent != 1 || dropped != 0 {
		t.Fatalf("sent %d, dropped %d", sent, dropped)
	}
}

func TestTwoCutDatagramsOfOnePortAreToldApart(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)
	other := strings.Repeat("ijklmnop", 375)
	first, second := pieces(7, 40000, at(far), big, 1400), pieces(8, 40000, at(far), other, 1400)

	for index := range first {
		f.Send(first[index])
		f.Send(second[index])
	}

	said, _ := heard(t, far)
	more, _ := heard(t, far)
	if said != big || more != other {
		t.Fatalf("the addressee got %d and %d byte(s), mixed: %v", len(said), len(more), said != big)
	}
}

func TestAPieceWithoutItsHeadOpensNoSocket(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)

	if !f.Send(pieces(7, 40000, at(far), big, 1400)[2]) {
		t.Fatal("a piece of a datagram was returned to the tunnel")
	}

	if !deaf(far, 300*time.Millisecond) {
		t.Fatal("a piece left as a datagram of its own")
	}

	if sent, count := f.Sent.Load(), f.Count(); sent != 0 || count != 0 {
		t.Fatalf("sent %d, %d socket(s) open for a piece without its head", sent, count)
	}
}

func TestADatagramMissingAPieceIsDroppedWhole(t *testing.T) {
	f, _ := forwarder(t, 1420, Idle, 20*time.Millisecond, nil)
	f.pieces.mu.Lock()
	f.pieces.wait = 60 * time.Millisecond
	f.pieces.mu.Unlock()
	far := peer(t)
	parts := pieces(7, 40000, at(far), big, 1400)

	f.Send(parts[0])
	f.Send(parts[2])

	for attempt := 0; attempt < 150 && f.Dropped.Load() != 1; attempt++ {
		time.Sleep(20 * time.Millisecond)
	}

	if !deaf(far, 100*time.Millisecond) {
		t.Fatal("a datagram left without one of its pieces")
	}

	if sent, dropped, count := f.Sent.Load(), f.Dropped.Load(), f.Count(); sent != 0 || dropped != 1 || count != 0 {
		t.Fatalf("sent %d, dropped %d, %d socket(s) open", sent, dropped, count)
	}
}

func TestOnlySoManyCutDatagramsAreHeldAtOnce(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)

	for id := 1; id <= maxCut+1; id++ {
		f.Send(pieces(uint16(id), 40000, at(far), big, 1400)[0])
	}

	if dropped := f.Dropped.Load(); dropped != 1 {
		t.Fatalf("%d datagram(s) were given up of 1", dropped)
	}

	for _, piece := range pieces(uint16(maxCut+1), 40000, at(far), big, 1400)[1:] {
		f.Send(piece)
	}

	if said, _ := heard(t, far); said != big {
		t.Fatalf("the newest datagram came as %d byte(s) of %d", len(said), len(big))
	}
}

func TestAPieceThatDoesNotFitItsDatagramDropsIt(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)
	parts := pieces(7, 40000, at(far), big, 1400)
	odd := append([]byte(nil), parts[1][:len(parts[1])-3]...)

	f.Send(parts[0])
	f.Send(odd)
	f.Send(parts[2])

	if !deaf(far, 300*time.Millisecond) {
		t.Fatal("a datagram left with a piece that does not fit it")
	}

	if sent, dropped := f.Sent.Load(), f.Dropped.Load(); sent != 0 || dropped != 1 {
		t.Fatalf("sent %d, dropped %d", sent, dropped)
	}
}

func TestOnlyAPieceOfACutDatagramIsCalledOne(t *testing.T) {
	to := netip.AddrPortFrom(loopback, 9)
	parts := pieces(7, 40000, to, big, 1400)

	if Cut(outgoing(40000, to, "whole")) || Later(outgoing(40000, to, "whole")) {
		t.Fatal("a whole datagram was called a piece")
	}

	if !Cut(parts[0]) || Later(parts[0]) {
		t.Fatal("the head of a cut datagram was not called its head")
	}

	if !Cut(parts[1]) || !Later(parts[1]) || !Cut(parts[2]) || !Later(parts[2]) {
		t.Fatal("a later piece of a cut datagram was not called one")
	}
}

func TestALaterPieceGoesWhereTheHeadWent(t *testing.T) {
	var heads Heads
	to := netip.AddrPortFrom(loopback, 9)
	aside, kept := pieces(7, 40000, to, big, 1400), pieces(8, 40000, to, big, 1400)

	heads.Note(aside[0], true, 0)
	heads.Note(kept[0], false, 0)

	if went, known := heads.Went(aside[2], int64(time.Second)); !went || !known {
		t.Fatalf("a piece of a datagram that left past the tunnel: aside %v, known %v", went, known)
	}

	if went, known := heads.Went(kept[1], int64(time.Second)); went || !known {
		t.Fatalf("a piece of a datagram that rides the tunnel: aside %v, known %v", went, known)
	}

	if _, known := heads.Went(pieces(9, 40000, to, big, 1400)[1], 0); known {
		t.Fatal("a piece whose head never came is said to follow one")
	}

	if _, known := heads.Went(aside[1], int64(cutWait)+1); known {
		t.Fatal("a head is still remembered after its pieces had their time")
	}
}

func TestTheHeadsRememberedStayFew(t *testing.T) {
	var heads Heads
	to := netip.AddrPortFrom(loopback, 9)

	for id := 1; id <= maxCut*4; id++ {
		heads.Note(pieces(uint16(id), 40000, to, big, 1400)[0], true, int64(id))
	}

	if held := len(heads.items); held > maxCut {
		t.Fatalf("%d head(s) are remembered of %d at most", held, maxCut)
	}

	if went, known := heads.Went(pieces(uint16(maxCut*4), 40000, to, big, 1400)[1], int64(maxCut*4)); !went || !known {
		t.Fatal("the newest head was forgotten")
	}
}
