/* SPDX-License-Identifier: MIT
 *
 * Bind that hands every socket it opens to the host before the engine sends on it (AmneziaGeo).
 */

package protect

import (
	"sync/atomic"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
)

// Bind отдаёт хосту каждый открытый сокет раньше, чем движок получит его на руки.
type Bind struct {
	conn.Bind
	protect   func(fd int) bool
	protected atomic.Bool
}

// New оборачивает привязку защитником, который отпускает сокет мимо туннеля.
func New(inner conn.Bind, protect func(fd int) bool) *Bind {
	return &Bind{Bind: inner, protect: protect}
}

// Open открывает сокеты привязки и отдаёт их защитнику.
func (b *Bind) Open(port uint16) ([]conn.ReceiveFunc, uint16, error) {
	b.protected.Store(false)
	fns, actual, err := b.Bind.Open(port)
	if err != nil {
		return fns, actual, err
	}

	opened := sockets(b.Bind)
	taken := 0
	for _, fd := range opened {
		if b.protect(fd) {
			taken++
		}
	}
	b.protected.Store(taken > 0 && taken == len(opened))
	return fns, actual, nil
}

// Protected говорит, взял ли защитник все сокеты, открытые последним разом.
func (b *Bind) Protected() bool {
	return b.protected.Load()
}

// Сокеты, которые привязка держит открытыми.
func sockets(bind conn.Bind) []int {
	peek, ok := bind.(conn.PeekLookAtSocketFd)
	if !ok {
		return nil
	}

	var fds []int
	for _, look := range []func() (int, error){peek.PeekLookAtSocketFd4, peek.PeekLookAtSocketFd6} {
		if fd, held := socket(look); held {
			fds = append(fds, fd)
		}
	}
	return fds
}

// Сокет одного семейства, если привязка его держит.
func socket(look func() (int, error)) (fd int, held bool) {
	defer func() {
		if recover() != nil {
			fd, held = -1, false
		}
	}()

	fd, err := look()
	return fd, err == nil
}
