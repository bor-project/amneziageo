package main

import (
	"bytes"
	"fmt"
	"net/netip"

	M "github.com/sagernet/sing/common/metadata"
)

var unspecified = netip.AddrFrom4([4]byte{})

// Wraps one datagram in the header the relay of the local proxy reads the destination from.
func packDatagram(destination M.Socksaddr, payload []byte) []byte {
	return append(appendAddress([]byte{0, 0, 0}, destination), payload...)
}

// Reads back the address a datagram came from and what it carried.
func unpackDatagram(raw []byte) (M.Socksaddr, []byte, error) {
	if len(raw) < 4 {
		return M.Socksaddr{}, nil, fmt.Errorf("a datagram of %d bytes", len(raw))
	}

	if raw[2] != 0 {
		return M.Socksaddr{}, nil, fmt.Errorf("a datagram in fragments")
	}

	reader := bytes.NewReader(raw[3:])
	source, err := readAddress(reader)
	if err != nil {
		return M.Socksaddr{}, nil, err
	}

	return source, raw[len(raw)-reader.Len():], nil
}

func netipAddrFromSlice(raw []byte) (netip.Addr, bool) {
	address, ok := netip.AddrFromSlice(raw)
	if !ok {
		return netip.Addr{}, false
	}

	return address.Unmap(), true
}
