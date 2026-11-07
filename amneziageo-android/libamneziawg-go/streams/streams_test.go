/* SPDX-License-Identifier: MIT
 *
 * Tests of the streams the stack takes off the tun (AmneziaGeo).
 */

package streams

import (
	"context"
	"testing"
	"time"

	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
	"gvisor.dev/gvisor/pkg/waiter"
)

const nic tcpip.NICID = 1

var (
	device = tcpip.AddrFrom4([4]byte{172, 29, 38, 2})
	remote = tcpip.AddrFrom4([4]byte{93, 184, 216, 34})
	peer   = tcpip.AddrFrom4([4]byte{172, 29, 38, 1})
)

// Стек, устроенный как стек движка: чужие адреса, ответ с них же, каждый SYN принимается.
func taking(t *testing.T) (*stack.Stack, *channel.Endpoint) {
	t.Helper()
	link := channel.New(16, 1500, "")
	s := stack.New(stack.Options{
		NetworkProtocols:   []stack.NetworkProtocolFactory{ipv4.NewProtocol},
		TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol},
	})
	if err := s.CreateNIC(nic, link); err != nil {
		t.Fatal(err)
	}

	s.SetPromiscuousMode(nic, true)
	s.SetSpoofing(nic, true)
	s.SetRouteTable([]tcpip.Route{{Destination: header.IPv4EmptySubnet, NIC: nic}})
	forwarder := tcp.NewForwarder(s, 0, 16, func(request *tcp.ForwarderRequest) {
		var queue waiter.Queue
		if _, err := request.CreateEndpoint(&queue); err != nil {
			request.Complete(true)
			return
		}

		request.Complete(false)
	})
	s.SetTransportProtocolHandler(tcp.ProtocolNumber, forwarder.HandlePacket)
	t.Cleanup(func() {
		s.Close()
		link.Close()
	})
	return s, link
}

// Сегмент IPv4 с правильными суммами.
func segment(src, dst tcpip.Address, srcPort, dstPort uint16, flags header.TCPFlags, seq, ack uint32) []byte {
	packet := make([]byte, header.IPv4MinimumSize+header.TCPMinimumSize)
	ip := header.IPv4(packet)
	ip.Encode(&header.IPv4Fields{
		TotalLength: uint16(len(packet)),
		TTL:         64,
		Protocol:    uint8(header.TCPProtocolNumber),
		SrcAddr:     src,
		DstAddr:     dst,
	})
	ip.SetChecksum(^ip.CalculateChecksum())
	segment := header.TCP(packet[header.IPv4MinimumSize:])
	segment.Encode(&header.TCPFields{
		SrcPort:    srcPort,
		DstPort:    dstPort,
		SeqNum:     seq,
		AckNum:     ack,
		DataOffset: header.TCPMinimumSize,
		Flags:      flags,
		WindowSize: 65535,
	})
	sum := header.PseudoHeaderChecksum(header.TCPProtocolNumber, src, dst, header.TCPMinimumSize)
	segment.SetChecksum(^segment.CalculateChecksum(sum))
	return packet
}

// Отдаёт сегмент стеку и ждёт его ответа.
func answer(t *testing.T, link *channel.Endpoint, packet []byte) header.TCP {
	t.Helper()
	pkt := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(packet)})
	link.InjectInbound(ipv4.ProtocolNumber, pkt)
	pkt.DecRef()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	out := link.ReadContext(ctx)
	if out.IsNil() {
		t.Fatal("the stack did not answer")
	}

	defer out.DecRef()
	view := out.ToView()
	defer view.Release()
	reply := append([]byte(nil), view.AsSlice()...)
	return header.TCP(reply[header.IPv4(reply).HeaderLength():])
}

func TestTheOpeningOfAStreamGoesToTheStack(t *testing.T) {
	s, _ := taking(t)

	if !Held(s, nic, segment(device, remote, 40000, 443, header.TCPFlagSyn, 1000, 0)) {
		t.Fatal("a stream the device opens did not go to the stack")
	}
}

func TestTheRestOfAStreamTheStackTookGoesToIt(t *testing.T) {
	s, link := taking(t)
	reply := answer(t, link, segment(device, remote, 40000, 443, header.TCPFlagSyn, 1000, 0))
	if reply.Flags() != header.TCPFlagSyn|header.TCPFlagAck {
		t.Fatalf("the stack answered the opening with %v", reply.Flags())
	}

	if !Held(s, nic, segment(device, remote, 40000, 443, header.TCPFlagAck, 1001, reply.SequenceNumber()+1)) {
		t.Fatal("the next segment of a stream the stack took did not go to it")
	}
}

func TestTheAnswerToAStreamThePeerOpenedStaysOff(t *testing.T) {
	s, link := taking(t)
	answer(t, link, segment(device, remote, 40000, 443, header.TCPFlagSyn, 1000, 0))

	if Held(s, nic, segment(device, peer, 5555, 50000, header.TCPFlagSyn|header.TCPFlagAck, 7000, 3001)) {
		t.Fatal("the answer to a connection the peer opened went to the stack")
	}

	if Held(s, nic, segment(device, peer, 5555, 50000, header.TCPFlagAck|header.TCPFlagPsh, 7001, 3001)) {
		t.Fatal("the data of a connection the peer opened went to the stack")
	}
}

func TestAStreamOnOtherPortsIsNotTaken(t *testing.T) {
	s, link := taking(t)
	answer(t, link, segment(device, remote, 40000, 443, header.TCPFlagSyn, 1000, 0))

	if Held(s, nic, segment(device, remote, 40001, 443, header.TCPFlagAck, 1001, 1)) {
		t.Fatal("a segment of another port pair went to the stack")
	}
}

func TestWhatIsNoStreamIsNotTaken(t *testing.T) {
	s, _ := taking(t)
	short := segment(device, remote, 40000, 443, header.TCPFlagSyn, 1000, 0)[:30]
	datagram := segment(device, remote, 40000, 443, header.TCPFlagSyn, 1000, 0)
	datagram[9] = uint8(header.UDPProtocolNumber)

	if Held(s, nic, short) || Held(s, nic, datagram) || Held(s, nic, nil) {
		t.Fatal("a cut packet or a datagram went to the stack")
	}
}
