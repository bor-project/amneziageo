/* SPDX-License-Identifier: MIT
 *
 * Tells the segments of the streams the stack takes off the tun from the rest (AmneziaGeo).
 */

package streams

import (
	"encoding/binary"

	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
)

// Берёт ли стек сегмент IPv4: начало потока от устройства и всё, что идёт по соединению, которое стек держит.
func Held(s *stack.Stack, nic tcpip.NICID, packet []byte) bool {
	if len(packet) < header.IPv4MinimumSize || packet[9] != uint8(header.TCPProtocolNumber) {
		return false
	}

	head := int(packet[0]&0x0f) * 4
	if head < header.IPv4MinimumSize || len(packet) < head+header.TCPMinimumSize {
		return false
	}

	flags := header.TCPFlags(packet[head+13])
	if flags&header.TCPFlagSyn != 0 && flags&header.TCPFlagAck == 0 {
		return true
	}

	id := stack.TransportEndpointID{
		LocalPort:     binary.BigEndian.Uint16(packet[head+2 : head+4]),
		LocalAddress:  tcpip.AddrFrom4Slice(packet[16:20]),
		RemotePort:    binary.BigEndian.Uint16(packet[head : head+2]),
		RemoteAddress: tcpip.AddrFrom4Slice(packet[12:16]),
	}
	return s.FindTransportEndpoint(ipv4.ProtocolNumber, tcp.ProtocolNumber, id, nic) != nil
}
