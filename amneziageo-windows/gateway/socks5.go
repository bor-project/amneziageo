package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"time"

	M "github.com/sagernet/sing/common/metadata"
)

const (
	version5         = 0x05
	noAuth           = 0x00
	commandConnect   = 0x01
	commandAssociate = 0x03
	addressIpV4      = 0x01
	addressName      = 0x03
	addressIpV6      = 0x04
	replyOk          = 0x00
	greetTimeout     = 10 * time.Second
)

// Opens the command given at the local proxy and answers with the connection and the address it bound.
func dial(proxy string, command byte, destination M.Socksaddr) (net.Conn, M.Socksaddr, error) {
	conn, err := net.DialTimeout("tcp", proxy, greetTimeout)
	if err != nil {
		return nil, M.Socksaddr{}, fmt.Errorf("reach the local proxy: %w", err)
	}

	conn.SetDeadline(time.Now().Add(greetTimeout))
	bound, err := greet(conn, command, destination)
	if err != nil {
		conn.Close()
		return nil, M.Socksaddr{}, err
	}

	conn.SetDeadline(time.Time{})
	return conn, bound, nil
}

func greet(conn net.Conn, command byte, destination M.Socksaddr) (M.Socksaddr, error) {
	if _, err := conn.Write([]byte{version5, 1, noAuth}); err != nil {
		return M.Socksaddr{}, err
	}

	answer := make([]byte, 2)
	if _, err := io.ReadFull(conn, answer); err != nil {
		return M.Socksaddr{}, err
	}

	if answer[0] != version5 || answer[1] != noAuth {
		return M.Socksaddr{}, fmt.Errorf("the local proxy asks for an account")
	}

	if _, err := conn.Write(appendAddress([]byte{version5, command, 0}, destination)); err != nil {
		return M.Socksaddr{}, err
	}

	head := make([]byte, 3)
	if _, err := io.ReadFull(conn, head); err != nil {
		return M.Socksaddr{}, err
	}

	if head[1] != replyOk {
		return M.Socksaddr{}, fmt.Errorf("the local proxy refused with %d", head[1])
	}

	return readAddress(conn)
}

// Opens a datagram relay at the local proxy and answers with the control connection and the address to send to.
func associate(proxy string) (net.Conn, M.Socksaddr, error) {
	control, bound, err := dial(proxy, commandAssociate, M.SocksaddrFrom(unspecified, 0))
	if err != nil {
		return nil, M.Socksaddr{}, err
	}

	// A proxy that answers with nothing in particular relays on the address it was reached at.
	if !bound.Addr.IsValid() || bound.Addr.IsUnspecified() {
		host, _, splitErr := net.SplitHostPort(control.RemoteAddr().String())
		if splitErr != nil {
			control.Close()
			return nil, M.Socksaddr{}, splitErr
		}

		bound = M.ParseSocksaddrHostPort(host, bound.Port)
	}

	return control, bound, nil
}

func appendAddress(head []byte, address M.Socksaddr) []byte {
	switch {
	case address.IsFqdn():
		head = append(head, addressName, byte(len(address.Fqdn)))
		head = append(head, address.Fqdn...)
	case address.Addr.Is4():
		octets := address.Addr.As4()
		head = append(head, addressIpV4)
		head = append(head, octets[:]...)
	default:
		octets := address.Addr.As16()
		head = append(head, addressIpV6)
		head = append(head, octets[:]...)
	}

	return binary.BigEndian.AppendUint16(head, address.Port)
}

func readAddress(reader io.Reader) (M.Socksaddr, error) {
	kind := make([]byte, 1)
	if _, err := io.ReadFull(reader, kind); err != nil {
		return M.Socksaddr{}, err
	}

	length, err := addressLength(reader, kind[0])
	if err != nil {
		return M.Socksaddr{}, err
	}

	raw := make([]byte, length+2)
	if _, err = io.ReadFull(reader, raw); err != nil {
		return M.Socksaddr{}, err
	}

	return parseAddress(kind[0], raw)
}

func addressLength(reader io.Reader, kind byte) (int, error) {
	switch kind {
	case addressIpV4:
		return 4, nil
	case addressIpV6:
		return 16, nil
	case addressName:
		size := make([]byte, 1)
		if _, err := io.ReadFull(reader, size); err != nil {
			return 0, err
		}

		return int(size[0]), nil
	default:
		return 0, fmt.Errorf("the local proxy named an address of kind %d", kind)
	}
}

// Reads one address of the kind given followed by its port.
func parseAddress(kind byte, raw []byte) (M.Socksaddr, error) {
	port := binary.BigEndian.Uint16(raw[len(raw)-2:])
	body := raw[:len(raw)-2]
	if kind == addressName {
		return M.ParseSocksaddrHostPort(string(body), port), nil
	}

	address, ok := netipAddrFromSlice(body)
	if !ok {
		return M.Socksaddr{}, fmt.Errorf("the local proxy named an address of %d bytes", len(body))
	}

	return M.SocksaddrFrom(address, port), nil
}
