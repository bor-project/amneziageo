//go:build android

/* SPDX-License-Identifier: MIT
 *
 * Hands the datagrams a direct verdict names to the sockets that leave past the tunnel (AmneziaGeo).
 */

package main

import (
	"sync/atomic"

	"github.com/bor-project/amneziageo/libamneziawg-go/datagram"
)

// Размер tun, когда его не удалось спросить.
const defaultTunMtu = 1420

// Куда возвращать ответ: это тот же системный tun, что читает движок.
type tun2Writer interface {
	Write(bufs [][]byte, offset int) (int, error)
	MTU() (int, error)
}

// Пересыльщик датаграмм с вердиктом «мимо туннеля»: его сокеты защищаются от туннеля до первой отправки.
func newForwarder(w tun2Writer, protect *atomic.Pointer[func(int) bool]) *datagram.Forwarder {
	mtu, err := w.MTU()
	if err != nil || mtu < 576 {
		mtu = defaultTunMtu
	}
	return datagram.New(w, mtu, protectControl(protect))
}
