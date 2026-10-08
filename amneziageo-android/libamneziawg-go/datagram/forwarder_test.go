/* SPDX-License-Identifier: MIT
 *
 * Tests of the sockets the datagrams of an app leave from past the tunnel (AmneziaGeo).
 */

package datagram

import (
	"encoding/binary"
	"net"
	"net/netip"
	"strings"
	"sync/atomic"
	"syscall"
	"testing"
	"time"
)

var (
	// Адрес приложения в туннеле.
	app = netip.MustParseAddr("10.8.0.2")
	// Адрес, на котором стоят чужие стороны.
	loopback = netip.MustParseAddr("127.0.0.1")
)

// Tun, который складывает вписанные пакеты.
type sink struct {
	packets chan []byte
}

func (s *sink) Write(bufs [][]byte, offset int) (int, error) {
	for _, buf := range bufs {
		s.packets <- append([]byte(nil), buf[offset:]...)
	}
	return len(bufs), nil
}

// Пакет, вписанный в tun: кто его прислал, кому он и что в нём.
type answer struct {
	from, to netip.AddrPort
	payload  string
	sound    bool
}

// Пересыльщик со своим tun.
func forwarder(t *testing.T, mtu int, idle, every time.Duration, control Control) (*Forwarder, *sink) {
	t.Helper()
	tun := &sink{packets: make(chan []byte, 64)}
	f := start(tun, mtu, control, idle, every)
	t.Cleanup(f.Close)
	return f, tun
}

// Пересыльщик с обычными сроками и без отметки сокетов.
func plain(t *testing.T) (*Forwarder, *sink) {
	t.Helper()
	return forwarder(t, 1420, Idle, sweepEvery, nil)
}

// Сокет на петле, который играет чужую сторону.
func peer(t *testing.T) *net.UDPConn {
	t.Helper()
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	return conn
}

// Адрес сокета чужой стороны.
func at(conn *net.UDPConn) netip.AddrPort {
	return conn.LocalAddr().(*net.UDPAddr).AddrPort()
}

// Пакет IPv4 с датаграммой от приложения.
func outgoing(port uint16, to netip.AddrPort, payload string) []byte {
	packet := make([]byte, 28+len(payload))
	packet[0] = 0x45
	binary.BigEndian.PutUint16(packet[2:4], uint16(len(packet)))
	packet[8] = 64
	packet[9] = syscall.IPPROTO_UDP
	source, target := app.As4(), to.Addr().As4()
	copy(packet[12:16], source[:])
	copy(packet[16:20], target[:])
	binary.BigEndian.PutUint16(packet[20:22], port)
	binary.BigEndian.PutUint16(packet[22:24], to.Port())
	binary.BigEndian.PutUint16(packet[24:26], uint16(8+len(payload)))
	copy(packet[28:], payload)
	return packet
}

// Датаграмма, которая дошла до чужой стороны, и адрес, с которого она пришла.
func heard(t *testing.T, conn *net.UDPConn) (string, netip.AddrPort) {
	t.Helper()
	buffer := make([]byte, 2048)
	conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	n, from, err := conn.ReadFromUDPAddrPort(buffer)
	if err != nil {
		t.Fatalf("nothing came to %v: %v", conn.LocalAddr(), err)
	}
	return string(buffer[:n]), netip.AddrPortFrom(from.Addr().Unmap(), from.Port())
}

// Говорит, что до чужой стороны за срок ничего не дошло.
func deaf(conn *net.UDPConn, wait time.Duration) bool {
	conn.SetReadDeadline(time.Now().Add(wait))
	_, _, err := conn.ReadFromUDPAddrPort(make([]byte, 2048))
	return err != nil
}

// Шлёт датаграмму с сокета чужой стороны на порт, под которым приложение видно снаружи.
func reply(t *testing.T, conn *net.UDPConn, port uint16, payload string) {
	t.Helper()
	if _, err := conn.WriteToUDPAddrPort([]byte(payload), netip.AddrPortFrom(loopback, port)); err != nil {
		t.Fatal(err)
	}
}

// Сумма шестнадцатибитных слов с переносами.
func folded(words []byte, sum uint32) uint16 {
	for i := 0; i+1 < len(words); i += 2 {
		sum += uint32(binary.BigEndian.Uint16(words[i : i+2]))
	}
	if len(words)%2 == 1 {
		sum += uint32(words[len(words)-1]) << 8
	}
	for sum>>16 != 0 {
		sum = (sum & 0xffff) + (sum >> 16)
	}
	return uint16(sum)
}

// Разбирает пакет, вписанный в tun.
func parsed(t *testing.T, packet []byte) answer {
	t.Helper()
	if len(packet) < 28 || packet[0] != 0x45 || packet[9] != syscall.IPPROTO_UDP {
		t.Fatalf("what was written to the tun is not a datagram: % x", packet)
	}
	if total := int(binary.BigEndian.Uint16(packet[2:4])); total != len(packet) {
		t.Fatalf("the packet says %d byte(s) and has %d", total, len(packet))
	}
	if length := int(binary.BigEndian.Uint16(packet[24:26])); length != len(packet)-20 {
		t.Fatalf("the datagram says %d byte(s) and has %d", length, len(packet)-20)
	}

	pseudo := uint32(syscall.IPPROTO_UDP) + uint32(len(packet)-20)
	pseudo += uint32(folded(packet[12:20], 0))
	return answer{
		from:    netip.AddrPortFrom(netip.AddrFrom4([4]byte(packet[12:16])), binary.BigEndian.Uint16(packet[20:22])),
		to:      netip.AddrPortFrom(netip.AddrFrom4([4]byte(packet[16:20])), binary.BigEndian.Uint16(packet[22:24])),
		payload: string(packet[28:]),
		sound:   folded(packet[:20], 0) == 0xffff && folded(packet[20:], pseudo) == 0xffff,
	}
}

// Пакет, который пересыльщик вписал в tun.
func written(t *testing.T, tun *sink) answer {
	t.Helper()
	select {
	case packet := <-tun.packets:
		return parsed(t, packet)
	case <-time.After(3 * time.Second):
		t.Fatal("nothing was written to the tun")
		return answer{}
	}
}

// Ждёт, пока число открытых сокетов станет заданным.
func settled(f *Forwarder, count int) bool {
	for attempt := 0; attempt < 150 && f.Count() != count; attempt++ {
		time.Sleep(20 * time.Millisecond)
	}
	return f.Count() == count
}

func TestOnePortOfAnAppLeavesFromOneSocket(t *testing.T) {
	f, _ := plain(t)
	first, second := peer(t), peer(t)

	if !f.Send(outgoing(40000, at(first), "one")) || !f.Send(outgoing(40000, at(second), "two")) {
		t.Fatal("a datagram was returned to the tunnel")
	}

	said, seen := heard(t, first)
	more, again := heard(t, second)
	if said != "one" || more != "two" {
		t.Fatalf("the addressees got %q and %q", said, more)
	}

	if seen.Port() != again.Port() {
		t.Fatalf("one port of the app is seen as %d by one addressee and as %d by another", seen.Port(), again.Port())
	}

	if count := f.Count(); count != 1 {
		t.Fatalf("%d socket(s) are open for one port of the app", count)
	}

	if sent := f.Sent.Load(); sent != 2 {
		t.Fatalf("%d datagram(s) are counted as sent of 2", sent)
	}
}

func TestAnAnswerFromAnAddressNotWrittenToComesBack(t *testing.T) {
	f, tun := plain(t)
	known, stranger := peer(t), peer(t)
	f.Send(outgoing(40000, at(known), "ask"))
	_, seen := heard(t, known)

	reply(t, stranger, seen.Port(), "hello")

	want := answer{from: at(stranger), to: netip.AddrPortFrom(app, 40000), payload: "hello", sound: true}
	if got := written(t, tun); got != want {
		t.Fatalf("the tun got %+v, not %+v", got, want)
	}

	if back := f.Back.Load(); back != 1 {
		t.Fatalf("%d answer(s) are counted of 1", back)
	}
}

func TestEachAnswerNamesItsSender(t *testing.T) {
	f, tun := plain(t)
	first, second := peer(t), peer(t)
	f.Send(outgoing(40000, at(first), "one"))
	f.Send(outgoing(40000, at(second), "two"))
	_, seen := heard(t, first)
	_, again := heard(t, second)

	reply(t, first, seen.Port(), "from the first")
	reply(t, second, again.Port(), "from the second")

	home := netip.AddrPortFrom(app, 40000)
	want := map[answer]bool{
		{from: at(first), to: home, payload: "from the first", sound: true}:   true,
		{from: at(second), to: home, payload: "from the second", sound: true}: true,
	}
	for range 2 {
		got := written(t, tun)
		if !want[got] {
			t.Fatalf("the tun got %+v", got)
		}
		delete(want, got)
	}
}

func TestAnotherPortOfTheAppLeavesFromAnotherSocket(t *testing.T) {
	f, tun := plain(t)
	far := peer(t)
	f.Send(outgoing(40000, at(far), "one"))
	_, seen := heard(t, far)
	f.Send(outgoing(40001, at(far), "two"))
	_, other := heard(t, far)

	if seen.Port() == other.Port() {
		t.Fatalf("two ports of the app are seen as one port %d", seen.Port())
	}

	if count := f.Count(); count != 2 {
		t.Fatalf("%d socket(s) are open for two ports of the app", count)
	}

	reply(t, far, other.Port(), "to the second")
	want := answer{from: at(far), to: netip.AddrPortFrom(app, 40001), payload: "to the second", sound: true}
	if got := written(t, tun); got != want {
		t.Fatalf("the tun got %+v, not %+v", got, want)
	}
}

func TestASocketTheAppStillSendsFromIsKept(t *testing.T) {
	f, _ := forwarder(t, 1420, 300*time.Millisecond, 50*time.Millisecond, nil)
	far := peer(t)
	f.Send(outgoing(40000, at(far), "start"))
	_, first := heard(t, far)

	for step := 0; step < 10; step++ {
		time.Sleep(100 * time.Millisecond)
		f.Send(outgoing(40000, at(far), "more"))
		if _, seen := heard(t, far); seen.Port() != first.Port() {
			t.Fatalf("the port the app is seen as went from %d to %d while it kept sending", first.Port(), seen.Port())
		}
	}
}

func TestASocketThatStillHearsAnswersIsKept(t *testing.T) {
	f, tun := forwarder(t, 1420, 300*time.Millisecond, 50*time.Millisecond, nil)
	far := peer(t)
	f.Send(outgoing(40000, at(far), "start"))
	_, first := heard(t, far)

	for step := 0; step < 10; step++ {
		time.Sleep(100 * time.Millisecond)
		reply(t, far, first.Port(), "tick")
		if got := written(t, tun); got.payload != "tick" {
			t.Fatalf("the tun got %+v", got)
		}
	}

	f.Send(outgoing(40000, at(far), "again"))
	if _, seen := heard(t, far); seen.Port() != first.Port() {
		t.Fatalf("the port the app is seen as went from %d to %d while answers kept coming", first.Port(), seen.Port())
	}
}

func TestASocketLeftSilentIsClosed(t *testing.T) {
	f, _ := forwarder(t, 1420, 200*time.Millisecond, 50*time.Millisecond, nil)
	far := peer(t)
	f.Send(outgoing(40000, at(far), "once"))
	_, first := heard(t, far)
	if count := f.Count(); count != 1 {
		t.Fatalf("%d socket(s) are open after a datagram left", count)
	}

	if !settled(f, 0) {
		t.Fatalf("%d socket(s) are still open after the port went silent", f.Count())
	}

	f.Send(outgoing(40000, at(far), "later"))
	if said, seen := heard(t, far); said != "later" || seen.Port() == first.Port() {
		t.Fatalf("after the silence the addressee got %q from port %d, the closed one was %d", said, seen.Port(), first.Port())
	}
}

func TestEverySocketIsMarkedOnceBeforeItSends(t *testing.T) {
	var marked atomic.Int32
	control := func(network, _ string, conn syscall.RawConn) error {
		if !strings.HasPrefix(network, "udp4") {
			t.Errorf("a socket of the network %q was opened", network)
		}
		return conn.Control(func(uintptr) { marked.Add(1) })
	}
	f, _ := forwarder(t, 1420, Idle, sweepEvery, control)
	first, second := peer(t), peer(t)

	f.Send(outgoing(40000, at(first), "one"))
	heard(t, first)
	if count := marked.Load(); count != 1 {
		t.Fatalf("%d socket(s) were marked before the first datagram left", count)
	}

	f.Send(outgoing(40000, at(second), "two"))
	heard(t, second)
	if count := marked.Load(); count != 1 {
		t.Fatalf("%d socket(s) were marked for one port of the app", count)
	}

	f.Send(outgoing(40001, at(first), "three"))
	heard(t, first)
	if count := marked.Load(); count != 2 {
		t.Fatalf("%d socket(s) were marked for two ports of the app", count)
	}
}

func TestASocketThatCouldNotBeMarkedSendsNothing(t *testing.T) {
	control := func(string, string, syscall.RawConn) error { return syscall.EPERM }
	f, _ := forwarder(t, 1420, Idle, sweepEvery, control)
	far := peer(t)

	if !f.Send(outgoing(40000, at(far), "one")) {
		t.Fatal("the datagram was returned to the tunnel")
	}

	if !deaf(far, 300*time.Millisecond) {
		t.Fatal("a datagram left from a socket that was not marked")
	}

	if refused, dropped, count := f.Refused.Load(), f.Dropped.Load(), f.Count(); refused != 1 || dropped != 1 || count != 0 {
		t.Fatalf("refused %d, dropped %d, %d socket(s) open", refused, dropped, count)
	}
}

func TestAnAnswerLongerThanTheTunIsDropped(t *testing.T) {
	f, tun := forwarder(t, 576, Idle, sweepEvery, nil)
	far := peer(t)
	f.Send(outgoing(40000, at(far), "ask"))
	_, seen := heard(t, far)

	reply(t, far, seen.Port(), strings.Repeat("x", 549))
	reply(t, far, seen.Port(), strings.Repeat("y", 548))

	if got := written(t, tun); got.payload != strings.Repeat("y", 548) || !got.sound {
		t.Fatalf("the tun got %d byte(s) starting with %q", len(got.payload), got.payload[:1])
	}

	if dropped := f.Dropped.Load(); dropped != 1 {
		t.Fatalf("%d answer(s) are counted as dropped of 1", dropped)
	}
}

func TestWhatIsNotADatagramIsReturned(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)
	stream := outgoing(40000, at(far), "stream")
	stream[9] = syscall.IPPROTO_TCP
	short := outgoing(40000, at(far), "")[:27]

	for name, packet := range map[string][]byte{"a stream": stream, "a cut packet": short, "nothing": nil} {
		if f.Send(packet) {
			t.Fatalf("%s was taken as a datagram", name)
		}
	}

	if count := f.Count(); count != 0 {
		t.Fatalf("%d socket(s) were opened for what is not a datagram", count)
	}
}

func TestAnAddresseeThatCannotBeWrittenToKeepsTheSocket(t *testing.T) {
	f, _ := plain(t)
	first, second := peer(t), peer(t)
	f.Send(outgoing(40000, at(first), "one"))
	_, seen := heard(t, first)

	if !f.Send(outgoing(40000, netip.AddrPortFrom(loopback, 0), "nowhere")) {
		t.Fatal("the datagram was returned to the tunnel")
	}

	if dropped := f.Dropped.Load(); dropped != 1 {
		t.Fatalf("%d datagram(s) are counted as dropped of 1", dropped)
	}

	f.Send(outgoing(40000, at(second), "two"))
	if _, again := heard(t, second); again.Port() != seen.Port() {
		t.Fatalf("the port the app is seen as went from %d to %d after a datagram could not be written", seen.Port(), again.Port())
	}
}

func TestNothingLeavesAfterTheForwarderIsClosed(t *testing.T) {
	f, _ := plain(t)
	far := peer(t)
	f.Send(outgoing(40000, at(far), "one"))
	heard(t, far)

	f.Close()
	if !f.Send(outgoing(40001, at(far), "two")) {
		t.Fatal("the datagram was returned to the tunnel")
	}

	if !deaf(far, 300*time.Millisecond) {
		t.Fatal("a datagram left after the forwarder was closed")
	}

	if count := f.Count(); count != 0 {
		t.Fatalf("%d socket(s) are open after the forwarder was closed", count)
	}
}
