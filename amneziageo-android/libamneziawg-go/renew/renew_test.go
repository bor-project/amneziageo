/* SPDX-License-Identifier: MIT
 *
 * Tests of the renewal of the session keys (AmneziaGeo).
 */

package renew

import (
	"crypto/ecdh"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	mrand "math/rand"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/tuntest"
)

// Маскировка, с которой ходит стенд.
const disguise = "jc=3\njmin=10\njmax=50\ns1=15\ns2=18\nh1=1000\nh2=2000\nh3=3000\nh4=4000\n"

// Сервер не берёт рукопожатие ближе 20 мс к прежнему, а после их потока секунду отвечает только на подтверждённые.
const (
	settle = 100 * time.Millisecond
	calm   = 1500 * time.Millisecond
)

var (
	near = netip.MustParseAddr("10.255.0.2")
	far  = netip.MustParseAddr("10.255.0.1")
)

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

// Движок на tun из каналов: поднимается по команде, принятое из туннеля никого не держит.
func engine(t *testing.T, settings string, closing bool) (*device.Device, *tuntest.ChannelTUN) {
	t.Helper()
	channel := tuntest.NewChannelTUN()
	<-channel.TUN().Events()
	go func() {
		for range channel.Inbound {
		}
	}()
	dev := device.NewDevice(channel.TUN(), conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	if closing {
		t.Cleanup(dev.Close)
	}
	if err := dev.IpcSet(settings); err != nil {
		t.Fatal(err)
	}
	if err := dev.Up(); err != nil {
		t.Fatal(err)
	}
	return dev, channel
}

// Клиент и сервер на петле.
type pair struct {
	client  *device.Device
	tun     *tuntest.ChannelTUN
	private string
	peers   []device.NoisePublicKey
}

// Поднимает сервер и клиента, который его знает.
func raise(t *testing.T, closing bool) *pair {
	t.Helper()
	clientPrivate, clientPublic := keys(t)
	serverPrivate, serverPublic := keys(t)
	port := freePort(t)
	engine(t, fmt.Sprintf("private_key=%s\nlisten_port=%d\n%spublic_key=%s\nallowed_ip=10.255.0.2/32\n",
		serverPrivate, port, disguise, clientPublic), closing)
	settings := fmt.Sprintf("private_key=%s\n%sreplace_peers=true\npublic_key=%s\nendpoint=127.0.0.1:%d\n"+
		"persistent_keepalive_interval=25\nallowed_ip=0.0.0.0/0\n\n", clientPrivate, disguise, serverPublic, port)
	client, channel := engine(t, settings, closing)
	return &pair{client: client, tun: channel, private: clientPrivate, peers: Peers(settings)}
}

// Кладёт пакет в туннель клиента; ложь, если движок его не взял.
func (p *pair) send(wait time.Duration) bool {
	select {
	case p.tun.Outbound <- tuntest.Ping(far, near):
		return true
	case <-time.After(wait):
		return false
	}
}

// Время последнего рукопожатия клиента в наносекундах.
func (p *pair) answered(t *testing.T) int64 {
	t.Helper()
	settings, err := p.client.IpcGet()
	if err != nil {
		t.Fatal(err)
	}
	total := int64(0)
	for _, line := range strings.Split(settings, "\n") {
		if value, found := strings.CutPrefix(line, "last_handshake_time_sec="); found {
			seconds, _ := strconv.ParseInt(value, 10, 64)
			total += seconds * int64(time.Second)
		}
		if value, found := strings.CutPrefix(line, "last_handshake_time_nsec="); found {
			nanos, _ := strconv.ParseInt(value, 10, 64)
			total += nanos
		}
	}
	return total
}

// Шлёт пакеты, пока сервер не ответит на рукопожатие позже названного; срок вмещает один повтор движка.
func (p *pair) await(t *testing.T, after int64) int64 {
	t.Helper()
	deadline := time.Now().Add(12 * time.Second)
	for time.Now().Before(deadline) {
		if !p.send(time.Second) {
			t.Fatal("the engine does not take a packet off its tun")
		}
		time.Sleep(20 * time.Millisecond)
		if seen := p.answered(t); seen > after {
			return seen
		}
	}
	t.Fatal("the server did not answer a handshake")
	return 0
}

// Ждёт занятым циклом: сон короче миллисекунды планировщик растягивает.
func spin(span time.Duration) {
	for start := time.Now(); time.Since(start) < span; {
	}
}

// Перемежает пакеты и сбросы ключей; говорит, сколько кругов прошло и дошло ли до конца.
func churn(p *pair, rounds int, renew func(), quiet time.Duration) (int, bool) {
	var done atomic.Int64
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		random := mrand.New(mrand.NewSource(1))
		for round := 0; round < rounds; round++ {
			if !p.send(quiet * 4) {
				return
			}
			spin(time.Duration(random.Intn(800)) * time.Microsecond)
			renew()
			done.Add(1)
		}
	}()
	last := int64(-1)
	for {
		select {
		case <-finished:
			return int(done.Load()), int(done.Load()) == rounds
		case <-time.After(quiet):
			now := done.Load()
			if now == last {
				return int(now), false
			}
			last = now
		}
	}
}

func TestPeersAreReadFromTheSettings(t *testing.T) {
	_, first := keys(t)
	_, second := keys(t)
	settings := "private_key=" + first + "\npublic_key=" + first + "\nendpoint=127.0.0.1:1\npublic_key=nothex\n public_key=" + second + " \n"
	peers := Peers(settings)
	if len(peers) != 2 {
		t.Fatalf("%d peers read, 2 named", len(peers))
	}
	if hex.EncodeToString(peers[0][:]) != first || hex.EncodeToString(peers[1][:]) != second {
		t.Fatal("the keys read are not the keys named")
	}
}

func TestAPeerTheEngineDoesNotKnowIsSkipped(t *testing.T) {
	p := raise(t, true)
	_, stranger := keys(t)
	if found := Sessions(p.client, Peers("public_key="+stranger+"\n")); found != 0 {
		t.Fatalf("%d sessions expired for a peer the engine does not know", found)
	}
}

func TestTheNextPacketOpensAHandshake(t *testing.T) {
	p := raise(t, true)
	first := p.await(t, 0)
	time.Sleep(settle)
	if found := Sessions(p.client, p.peers); found != 1 {
		t.Fatalf("%d sessions expired, 1 peer set", found)
	}
	if second := p.await(t, first); second <= first {
		t.Fatal("no handshake after the sessions expired")
	}
}

func TestRenewingWhileThePeerAnswersDoesNotStall(t *testing.T) {
	p := raise(t, true)
	p.await(t, 0)
	rounds := 6000
	done, whole := churn(p, rounds, func() { Sessions(p.client, p.peers) }, 3*time.Second)
	if !whole {
		t.Fatalf("the engine stood still at round %d of %d", done, rounds)
	}
	time.Sleep(calm)
	Sessions(p.client, p.peers)
	p.await(t, p.answered(t))
}
