//go:build android

/* SPDX-License-Identifier: MIT
 *
 * Carries the streams a direct verdict names past the tunnel (AmneziaGeo).
 */

package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
	"gvisor.dev/gvisor/pkg/waiter"
)

// Столько ждём ответа той стороны.
const tcpDialTimeout = 10 * time.Second

// Столько соединений ждут установки одновременно.
const tcpInFlight = 512

// Столько пакетов стек держит на выход.
const tcpQueue = 512

// Идентификатор единственного интерфейса стека.
const tcpNic tcpip.NICID = 1

// Терминирует поток с вердиктом «мимо туннеля» и переливает его в свой защищённый сокет.
type tcpForwarder struct {
	stack *stack.Stack
	link  *channel.Endpoint
	tun   tun2Writer

	protect *atomic.Pointer[func(int) bool]

	opened  atomic.Uint64
	refused atomic.Uint64
	live    atomic.Int64
	up      atomic.Uint64
	down    atomic.Uint64

	ctx    context.Context
	cancel context.CancelFunc
	once   sync.Once
}

func newTcpForwarder(w tun2Writer, mtu int, protect *atomic.Pointer[func(int) bool]) (*tcpForwarder, error) {
	ctx, cancel := context.WithCancel(context.Background())
	f := &tcpForwarder{tun: w, protect: protect, ctx: ctx, cancel: cancel}
	f.link = channel.New(tcpQueue, uint32(mtu), "")
	f.stack = stack.New(stack.Options{
		NetworkProtocols:   []stack.NetworkProtocolFactory{ipv4.NewProtocol},
		TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol},
	})

	if err := f.stack.CreateNIC(tcpNic, f.link); err != nil {
		cancel()
		return nil, errors.New(err.String())
	}

	// Пакет приходит на чужой адрес, и ответ уходит с него же.
	if err := f.stack.SetPromiscuousMode(tcpNic, true); err != nil {
		f.close()
		return nil, errors.New(err.String())
	}

	if err := f.stack.SetSpoofing(tcpNic, true); err != nil {
		f.close()
		return nil, errors.New(err.String())
	}

	f.stack.SetRouteTable([]tcpip.Route{{Destination: header.IPv4EmptySubnet, NIC: tcpNic}})
	handler := tcp.NewForwarder(f.stack, 0, tcpInFlight, f.accept)
	f.stack.SetTransportProtocolHandler(tcp.ProtocolNumber, handler.HandlePacket)
	go f.drain()
	return f, nil
}

// Отдаёт сегмент своему стеку; отказ возвращает пакет вызывающему.
func (f *tcpForwarder) send(packet []byte) bool {
	pkt := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(packet)})
	f.link.InjectInbound(ipv4.ProtocolNumber, pkt)
	pkt.DecRef()
	return true
}

// Ответы стека вписываются обратно в tun как обычные пакеты.
func (f *tcpForwarder) drain() {
	for {
		pkt := f.link.ReadContext(f.ctx)
		if pkt.IsNil() {
			return
		}

		view := pkt.ToView()
		_, err := f.tun.Write([][]byte{view.AsSlice()}, 0)
		view.Release()
		pkt.DecRef()
		if err != nil {
			return
		}
	}
}

func (f *tcpForwarder) accept(request *tcp.ForwarderRequest) {
	go f.serve(request)
}

// Сначала соединение наружу и только потом ответ клиенту: иначе он увидит установленную сессию, за
// которой ничего нет.
func (f *tcpForwarder) serve(request *tcp.ForwarderRequest) {
	id := request.ID()
	address := id.LocalAddress.As4()
	outbound, err := f.dial(&net.TCPAddr{IP: net.IPv4(address[0], address[1], address[2], address[3]), Port: int(id.LocalPort)})
	if err != nil {
		f.refused.Add(1)
		request.Complete(true)
		return
	}

	var queue waiter.Queue
	endpoint, cerr := request.CreateEndpoint(&queue)
	if cerr != nil {
		outbound.Close()
		f.refused.Add(1)
		request.Complete(true)
		return
	}

	request.Complete(false)
	f.opened.Add(1)
	f.live.Add(1)
	go f.pump(gonet.NewTCPConn(&queue, endpoint), outbound)
}

// Сокет наружу защищается до соединения, иначе поток уйдёт обратно в tun.
func (f *tcpForwarder) dial(target *net.TCPAddr) (net.Conn, error) {
	dialer := net.Dialer{
		Timeout:   tcpDialTimeout,
		KeepAlive: 30 * time.Second,
		Control:   protectControl(f.protect),
	}
	return dialer.Dial("tcp4", target.String())
}

// Стороны переливаются друг в друга, пока обе не закроются.
func (f *tcpForwarder) pump(inbound *gonet.TCPConn, outbound net.Conn) {
	defer func() {
		inbound.Close()
		outbound.Close()
		f.live.Add(-1)
	}()

	done := make(chan struct{})
	go func() {
		sent, _ := io.Copy(outbound, inbound)
		f.up.Add(uint64(sent))
		if half, ok := outbound.(interface{ CloseWrite() error }); ok {
			half.CloseWrite()
		}
		close(done)
	}()

	received, _ := io.Copy(inbound, outbound)
	f.down.Add(uint64(received))
	inbound.CloseWrite()
	<-done
}

// Счётчики потоков мимо туннеля.
func (f *tcpForwarder) stats() string {
	return fmt.Sprintf("%d stream(s), %d taken, %d refused, out %d KiB, in %d KiB",
		f.live.Load(), f.opened.Load(), f.refused.Load(), f.up.Load()/1024, f.down.Load()/1024)
}

func (f *tcpForwarder) close() {
	f.once.Do(func() {
		f.cancel()
		f.link.Close()
		f.stack.Close()
	})
}

// Ставит на сокет отметку «мимо туннеля» до соединения.
func protectControl(holder *atomic.Pointer[func(int) bool]) func(string, string, syscall.RawConn) error {
	return func(_, _ string, c syscall.RawConn) error {
		var kept error
		err := c.Control(func(fd uintptr) {
			if fn := holder.Load(); fn != nil {
				if !(*fn)(int(fd)) {
					kept = syscall.EPERM
				}
			}
		})
		if err != nil {
			return err
		}

		return kept
	}
}
