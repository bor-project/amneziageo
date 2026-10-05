/* SPDX-License-Identifier: MIT
 *
 * Tests of the bind that hands its sockets to the host (AmneziaGeo).
 */

package protect

import (
	"crypto/ecdh"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"slices"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"testing"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/tuntest"
)

// Маскировка, с которой ходит стенд.
const disguise = "jc=3\njmin=10\njmax=50\ns1=15\ns2=18\nh1=1000\nh2=2000\nh3=3000\nh4=4000\n"

// Пара ключей в шестнадцатеричной записи.
func keys(t *testing.T) (string, string) {
	t.Helper()
	private, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return hex.EncodeToString(private.Bytes()), hex.EncodeToString(private.PublicKey().Bytes())
}

// Свободный порт UDP на петле.
func freePort(t *testing.T) int {
	t.Helper()
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer socket.Close()
	return socket.LocalAddr().(*net.UDPAddr).Port
}

// Tun, с которым движок поднимается только по команде.
func quietTun() tun.Device {
	channel := tuntest.NewChannelTUN()
	<-channel.TUN().Events()
	return channel.TUN()
}

// Сервер, который знает клиента.
func server(t *testing.T, private string, client string, port int) {
	t.Helper()
	dev := device.NewDevice(quietTun(), conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	t.Cleanup(dev.Close)
	settings := fmt.Sprintf("private_key=%s\nlisten_port=%d\n%spublic_key=%s\nallowed_ip=10.255.0.2/32\n", private, port, disguise, client)
	if err := dev.IpcSet(settings); err != nil {
		t.Fatal(err)
	}
	if err := dev.Up(); err != nil {
		t.Fatal(err)
	}
}

// Настройки клиента, как их даёт перевод конфигурации.
func client(private string, serverKey string, port int) string {
	return fmt.Sprintf("private_key=%s\n%sreplace_peers=true\npublic_key=%s\nendpoint=127.0.0.1:%d\npersistent_keepalive_interval=25\nallowed_ip=0.0.0.0/0\n\n",
		private, disguise, serverKey, port)
}

// Ждёт рукопожатия пира.
func awaitHandshake(t *testing.T, dev *device.Device) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		settings, err := dev.IpcGet()
		if err != nil {
			t.Fatal(err)
		}
		for _, line := range strings.Split(settings, "\n") {
			value, found := strings.CutPrefix(line, "last_handshake_time_sec=")
			if !found {
				continue
			}
			if seconds, err := strconv.ParseInt(value, 10, 64); err == nil && seconds > 0 {
				return
			}
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("the server did not answer the handshake")
}

// Датаграмма клиента: порт, с которого она ушла, и когда пришла.
type arrival struct {
	port int
	at   time.Time
}

// Пересыльщик между клиентом и сервером, который помнит датаграммы клиента.
type relay struct {
	front *net.UDPConn
	back  *net.UDPConn
	mu    sync.Mutex
	seen  []arrival
	from  *net.UDPAddr
}

func newRelay(t *testing.T, server int) *relay {
	t.Helper()
	front, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	back, err := net.DialUDP("udp4", nil, &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: server})
	if err != nil {
		front.Close()
		t.Fatal(err)
	}
	r := &relay{front: front, back: back}
	t.Cleanup(func() {
		front.Close()
		back.Close()
	})
	go r.up()
	go r.down()
	return r
}

// Порт, на котором пересыльщик ждёт клиента.
func (r *relay) port() int {
	return r.front.LocalAddr().(*net.UDPAddr).Port
}

// Несёт датаграммы клиента серверу.
func (r *relay) up() {
	buffer := make([]byte, 2048)
	for {
		n, from, err := r.front.ReadFromUDP(buffer)
		if errors.Is(err, net.ErrClosed) {
			return
		}
		if err != nil {
			continue
		}
		r.mu.Lock()
		r.seen = append(r.seen, arrival{port: from.Port, at: time.Now()})
		r.from = from
		r.mu.Unlock()
		r.back.Write(buffer[:n])
	}
}

// Несёт ответы сервера клиенту.
func (r *relay) down() {
	buffer := make([]byte, 2048)
	for {
		n, err := r.back.Read(buffer)
		if errors.Is(err, net.ErrClosed) {
			return
		}
		if err != nil {
			continue
		}
		r.mu.Lock()
		to := r.from
		r.mu.Unlock()
		if to != nil {
			r.front.WriteToUDP(buffer[:n], to)
		}
	}
}

// Первая датаграмма с порта, которого нет среди названных.
func (r *relay) first(t *testing.T, known ...int) arrival {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		r.mu.Lock()
		for _, sent := range r.seen {
			if !slices.Contains(known, sent.port) {
				r.mu.Unlock()
				return sent
			}
		}
		r.mu.Unlock()
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("no datagram came from a new port")
	return arrival{}
}

// Хост, который берёт сокет не сразу и помнит, какой сокет и какой порт когда взял.
type host struct {
	wait time.Duration
	mu   sync.Mutex
	fds  []int
	took map[int]time.Time
}

func newHost(wait time.Duration) *host {
	return &host{wait: wait, took: make(map[int]time.Time)}
}

func (h *host) take(fd int) bool {
	time.Sleep(h.wait)
	h.mu.Lock()
	defer h.mu.Unlock()
	h.fds = append(h.fds, fd)
	h.took[bound(fd)] = time.Now()
	return true
}

// Сокеты, которые хост взял, по порядку.
func (h *host) taken() []int {
	h.mu.Lock()
	defer h.mu.Unlock()
	return slices.Clone(h.fds)
}

// Проверяет, что датаграмма ушла с сокета, который хост к тому времени взял.
func (h *host) held(t *testing.T, sent arrival) {
	t.Helper()
	h.mu.Lock()
	took, known := h.took[sent.port]
	h.mu.Unlock()
	if !known {
		t.Fatalf("a datagram left port %d, whose socket the host was never handed", sent.port)
	}
	if sent.at.Before(took) {
		t.Fatalf("a datagram left port %d %s before the host took its socket", sent.port, took.Sub(sent.at))
	}
}

// Порт, к которому привязан сокет.
func bound(fd int) int {
	address, err := syscall.Getsockname(fd)
	if err != nil {
		return 0
	}
	switch a := address.(type) {
	case *syscall.SockaddrInet4:
		return a.Port
	case *syscall.SockaddrInet6:
		return a.Port
	}
	return 0
}

// Сокеты обоих семейств настоящей привязки.
func both(t *testing.T, bind conn.Bind) []int {
	t.Helper()
	peek := bind.(conn.PeekLookAtSocketFd)
	v4, err := peek.PeekLookAtSocketFd4()
	if err != nil {
		t.Fatal(err)
	}
	v6, err := peek.PeekLookAtSocketFd6()
	if err != nil {
		t.Fatal(err)
	}
	return []int{v4, v6}
}

// Привязка без сокета IPv6.
type lone struct {
	conn.Bind
}

func (b lone) PeekLookAtSocketFd4() (int, error) {
	return b.Bind.(conn.PeekLookAtSocketFd).PeekLookAtSocketFd4()
}

func (b lone) PeekLookAtSocketFd6() (int, error) {
	var none *net.UDPConn
	_, err := none.SyscallConn()
	return -1, err
}

// Привязка, сокеты которой снаружи не видны.
type blind struct {
	conn.Bind
}

func TestOpenHandsOverEverySocketItOpened(t *testing.T) {
	owner := newHost(0)
	inner := conn.NewDefaultBind()
	bind := New(inner, owner.take)
	if _, _, err := bind.Open(0); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { bind.Close() })

	if open := both(t, inner); !slices.Equal(owner.taken(), open) {
		t.Fatalf("the host took %v of the sockets %v", owner.taken(), open)
	}
	if !bind.Protected() {
		t.Fatal("the bind does not tell that its sockets are taken")
	}
}

func TestEveryOpenHandsOverItsOwnSockets(t *testing.T) {
	owner := newHost(0)
	inner := conn.NewDefaultBind()
	bind := New(inner, owner.take)
	if _, _, err := bind.Open(0); err != nil {
		t.Fatal(err)
	}
	first := both(t, inner)
	if err := bind.Close(); err != nil {
		t.Fatal(err)
	}
	if _, _, err := bind.Open(0); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { bind.Close() })

	if want := append(first, both(t, inner)...); !slices.Equal(owner.taken(), want) {
		t.Fatalf("the host took %v of the sockets %v", owner.taken(), want)
	}
	if !bind.Protected() {
		t.Fatal("the bind does not tell that its sockets are taken")
	}
}

func TestOneRefusedSocketIsTold(t *testing.T) {
	asked := 0
	bind := New(conn.NewDefaultBind(), func(int) bool {
		asked++
		return asked == 1
	})
	fns, _, err := bind.Open(0)
	if err != nil || len(fns) == 0 {
		t.Fatalf("the bind did not open: %v", err)
	}
	t.Cleanup(func() { bind.Close() })

	if asked != 2 {
		t.Fatalf("the host was asked %d time(s)", asked)
	}
	if bind.Protected() {
		t.Fatal("the bind tells that its sockets are taken")
	}
}

func TestABindWithoutOneFamilyIsTaken(t *testing.T) {
	owner := newHost(0)
	inner := conn.NewDefaultBind()
	bind := New(lone{inner}, owner.take)
	if _, _, err := bind.Open(0); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { bind.Close() })

	if open := both(t, inner)[:1]; !slices.Equal(owner.taken(), open) {
		t.Fatalf("the host took %v of the sockets %v", owner.taken(), open)
	}
	if !bind.Protected() {
		t.Fatal("the bind does not tell that its socket is taken")
	}
}

func TestABindThatShowsNoSocketIsNotTaken(t *testing.T) {
	owner := newHost(0)
	bind := New(blind{conn.NewDefaultBind()}, owner.take)
	fns, _, err := bind.Open(0)
	if err != nil || len(fns) == 0 {
		t.Fatalf("the bind did not open: %v", err)
	}
	t.Cleanup(func() { bind.Close() })

	if len(owner.taken()) != 0 || bind.Protected() {
		t.Fatalf("the host took %v and the bind tells %v", owner.taken(), bind.Protected())
	}
}

func TestNoDatagramLeavesASocketBeforeTheHostTakesIt(t *testing.T) {
	serverPrivate, serverPublic := keys(t)
	clientPrivate, clientPublic := keys(t)
	listens := freePort(t)
	server(t, serverPrivate, clientPublic, listens)
	between := newRelay(t, listens)
	owner := newHost(200 * time.Millisecond)

	bind := New(conn.NewDefaultBind(), owner.take)
	dev := device.NewDevice(quietTun(), bind, device.NewLogger(device.LogLevelSilent, ""))
	t.Cleanup(dev.Close)
	if err := dev.IpcSet(client(clientPrivate, serverPublic, between.port())); err != nil {
		t.Fatal(err)
	}
	if err := dev.Up(); err != nil {
		t.Fatal(err)
	}

	started := between.first(t)
	owner.held(t, started)
	if !bind.Protected() {
		t.Fatal("the bind does not tell that its sockets are taken")
	}
	awaitHandshake(t, dev)

	if err := dev.IpcSet("listen_port=0\n"); err != nil {
		t.Fatal(err)
	}
	dev.SendKeepalivesToPeersWithCurrentKeypair()

	moved := between.first(t, started.port)
	owner.held(t, moved)
	if !bind.Protected() {
		t.Fatal("the bind does not tell that the sockets of the new port are taken")
	}
	if got := len(owner.taken()); got != 4 {
		t.Fatalf("the host took %d socket(s) over two ports", got)
	}
}
