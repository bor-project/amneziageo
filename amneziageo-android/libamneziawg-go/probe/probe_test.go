/* SPDX-License-Identifier: MIT
 *
 * Tests of the handshake probe (AmneziaGeo).
 */

package probe

import (
	"crypto/ecdh"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
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

// Сервер на пустом tun, который знает клиента.
func server(t *testing.T, own string, private string, client string, port int) {
	t.Helper()
	dev := device.NewDevice(newIdleTun(), conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	t.Cleanup(dev.Close)
	settings := fmt.Sprintf("private_key=%s\nlisten_port=%d\n%spublic_key=%s\nallowed_ip=10.255.0.2/32\n", private, port, own, client)
	if err := dev.IpcSet(settings); err != nil {
		t.Fatal(err)
	}
	if err := dev.Up(); err != nil {
		t.Fatal(err)
	}
}

// Настройки клиента, как их даёт перевод конфигурации.
func client(own string, private string, serverKey string, port int) string {
	return fmt.Sprintf("private_key=%s\nlisten_port=51820\n%sreplace_peers=true\npublic_key=%s\nendpoint=127.0.0.1:%d\npersistent_keepalive_interval=25\nallowed_ip=0.0.0.0/0\n\n",
		private, own, serverKey, port)
}

func TestSplitKeepsTheDeviceApartFromThePeers(t *testing.T) {
	own, peers := Split("private_key=aa\r\nlisten_port=51820\njc=3\nreplace_peers=true\npublic_key=bb\nendpoint=1.2.3.4:5\nallowed_ip=0.0.0.0/0\n\n")

	if own != "private_key=aa\njc=3\nreplace_peers=true\n" {
		t.Fatalf("the device part: %q", own)
	}
	if peers != "public_key=bb\nendpoint=1.2.3.4:5\nallowed_ip=0.0.0.0/0\n" {
		t.Fatalf("the peer part: %q", peers)
	}
}

func TestRunWithoutAPeerFails(t *testing.T) {
	private, _ := keys(t)

	if got := Run("private_key="+private+"\n", nil, time.Second, device.NewLogger(device.LogLevelSilent, "")); got != Failed {
		t.Fatalf("got %d", got)
	}
}

func TestRunHearsAServerThatAnswers(t *testing.T) {
	for name, own := range map[string]string{"plain": "", "disguised": disguise} {
		t.Run(name, func(t *testing.T) {
			serverPrivate, serverPublic := keys(t)
			clientPrivate, clientPublic := keys(t)
			port := freePort(t)
			server(t, own, serverPrivate, clientPublic, port)

			opened := 0
			started := time.Now()
			got := Run(client(own, clientPrivate, serverPublic, port), func(dev *device.Device) {
				opened++
				settings, err := dev.IpcGet()
				if err != nil || strings.Contains(settings, "public_key=") {
					t.Errorf("the socket is handed over after the peer is told: %v", err)
				}
			}, 5*time.Second, device.NewLogger(device.LogLevelSilent, ""))

			if got != Answered {
				t.Fatalf("got %d", got)
			}
			if opened != 1 {
				t.Fatalf("the socket was handed over %d times", opened)
			}
			if time.Since(started) > 3*time.Second {
				t.Fatalf("the answer took %s", time.Since(started))
			}
		})
	}
}

func TestRunTellsAServerThatKeepsSilent(t *testing.T) {
	clientPrivate, _ := keys(t)
	_, serverPublic := keys(t)
	port := freePort(t)

	started := time.Now()
	got := Run(client(disguise, clientPrivate, serverPublic, port), nil, 1200*time.Millisecond, device.NewLogger(device.LogLevelSilent, ""))

	if got != Silent {
		t.Fatalf("got %d", got)
	}
	if waited := time.Since(started); waited < time.Second || waited > 3*time.Second {
		t.Fatalf("the wait took %s", waited)
	}
}

func TestRunTellsAServerThatDoesNotKnowTheClient(t *testing.T) {
	serverPrivate, serverPublic := keys(t)
	_, known := keys(t)
	stranger, _ := keys(t)
	port := freePort(t)
	server(t, disguise, serverPrivate, known, port)

	if got := Run(client(disguise, stranger, serverPublic, port), nil, 1200*time.Millisecond, device.NewLogger(device.LogLevelSilent, "")); got != Silent {
		t.Fatalf("got %d", got)
	}
}
