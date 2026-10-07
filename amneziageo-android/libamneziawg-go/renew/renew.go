/* SPDX-License-Identifier: MIT
 *
 * Makes the next packet of an engine open a handshake without touching its identity (AmneziaGeo).
 */

package renew

import (
	"strings"

	"github.com/amnezia-vpn/amneziawg-go/v3/device"
)

// Peers читает из настроек открытые ключи пиров.
func Peers(settings string) []device.NoisePublicKey {
	var peers []device.NoisePublicKey
	for _, line := range strings.Split(settings, "\n") {
		value, found := strings.CutPrefix(strings.TrimSpace(line), "public_key=")
		if !found {
			continue
		}
		var key device.NoisePublicKey
		if key.FromHex(value) == nil {
			peers = append(peers, key)
		}
	}
	return peers
}

// Sessions сбрасывает ключи сеанса названных пиров и говорит, скольких движок знает.
func Sessions(dev *device.Device, peers []device.NoisePublicKey) int {
	found := 0
	for _, key := range peers {
		if peer := dev.LookupPeer(key); peer != nil {
			peer.ExpireCurrentKeypairs()
			found++
		}
	}
	return found
}
