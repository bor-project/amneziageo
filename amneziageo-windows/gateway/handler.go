package main

import (
	"context"
	"io"
	"net"
	"sync"
	"time"

	tun "github.com/sagernet/sing-tun"
	"github.com/sagernet/sing/common/buf"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"
)

const readBuffer = 65535

// What the stack terminates the local proxy opens again, so where a connection of a client leaves is decided
// by the rules of this machine and nothing here.
type handler struct {
	proxy string
	log   *lines

	access  sync.Mutex
	control net.Conn
	relay   M.Socksaddr
}

// Nothing rides the adapter without passing the local proxy, so no session is ever routed straight out.
func (h *handler) PrepareConnection(
	network string,
	source M.Socksaddr,
	destination M.Socksaddr,
	routeContext tun.DirectRouteContext,
	timeout time.Duration,
) (tun.DirectRouteDestination, error) {
	return nil, nil
}

func (h *handler) NewConnectionEx(
	ctx context.Context,
	conn net.Conn,
	source M.Socksaddr,
	destination M.Socksaddr,
	onClose N.CloseHandlerFunc,
) {
	upstream, _, err := dial(h.proxy, commandConnect, destination)
	if err != nil {
		conn.Close()
		h.log.Warn("tcp ", destination.String(), ": ", err)
		finish(onClose, err)
		return
	}

	finish(onClose, relay(conn, upstream))
}

func (h *handler) NewPacketConnectionEx(
	ctx context.Context,
	conn N.PacketConn,
	source M.Socksaddr,
	destination M.Socksaddr,
	onClose N.CloseHandlerFunc,
) {
	relayAddress, err := h.datagrams()
	if err != nil {
		conn.Close()
		h.log.Warn("udp ", destination.String(), ": ", err)
		finish(onClose, err)
		return
	}

	socket, err := net.DialUDP("udp", nil, relayAddress.UDPAddr())
	if err != nil {
		conn.Close()
		h.log.Warn("udp ", destination.String(), ": ", err)
		finish(onClose, err)
		return
	}

	defer socket.Close()

	go func() {
		toRelay(conn, socket)
		socket.Close()
	}()

	toClient(conn, socket)
	conn.Close()
	finish(onClose, nil)
}

// Where the datagrams of every flow go. One relay serves them all: a relay per flow would hold a connection to
// the local proxy per flow, and a client that opens many of them runs the machine out of ports. The proxy tells
// them apart by the port each flow sends from.
func (h *handler) datagrams() (M.Socksaddr, error) {
	h.access.Lock()
	defer h.access.Unlock()
	if h.control != nil {
		return h.relay, nil
	}

	control, address, err := associate(h.proxy)
	if err != nil {
		return M.Socksaddr{}, err
	}

	h.control = control
	h.relay = address
	go h.dropped(control)
	return address, nil
}

// The relay stands while the connection that asked for it does; once the proxy lets it go, the next datagram
// asks for a new one.
func (h *handler) dropped(control net.Conn) {
	io.Copy(io.Discard, control)
	control.Close()
	h.access.Lock()
	if h.control == control {
		h.control = nil
	}

	h.access.Unlock()
}

func finish(onClose N.CloseHandlerFunc, err error) {
	if onClose != nil {
		onClose(err)
	}
}

// Carries both directions until either end goes quiet, and answers with what ended it.
func relay(left net.Conn, right net.Conn) error {
	back := make(chan error, 1)
	go func() {
		_, err := io.Copy(right, left)
		back <- err
	}()

	_, err := io.Copy(left, right)
	left.Close()
	right.Close()
	if first := <-back; first != nil {
		return first
	}

	return err
}

func toRelay(conn N.PacketConn, socket *net.UDPConn) {
	for {
		buffer := buf.NewPacket()
		destination, err := conn.ReadPacket(buffer)
		if err != nil {
			buffer.Release()
			return
		}

		_, err = socket.Write(packDatagram(destination, buffer.Bytes()))
		buffer.Release()
		if err != nil {
			return
		}
	}
}

func toClient(conn N.PacketConn, socket *net.UDPConn) {
	raw := make([]byte, readBuffer)
	for {
		socket.SetReadDeadline(time.Now().Add(udpTimeout))
		read, err := socket.Read(raw)
		if err != nil {
			return
		}

		source, payload, err := unpackDatagram(raw[:read])
		if err != nil {
			continue
		}

		if err = conn.WritePacket(buf.As(payload), source); err != nil {
			return
		}
	}
}
