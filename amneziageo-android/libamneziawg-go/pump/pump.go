/* SPDX-License-Identifier: MIT
 *
 * Carries one direction of a stream and holds a buffer only while bytes are there to carry (AmneziaGeo).
 */

package pump

import (
	"io"
	"net"
	"sync"
	"sync/atomic"
	"syscall"
)

// Размер буфера переливки.
const BufferSize = 32 * 1024

// Буферы переливки, общие для всех потоков.
var buffers = sync.Pool{New: func() any {
	buffer := make([]byte, BufferSize)
	return &buffer
}}

// Буферы, которые сейчас на руках у потоков.
var held atomic.Int64

// Сколько буферов сейчас на руках у потоков.
func Held() int64 {
	return held.Load()
}

// Переливает одну сторону в другую до конца потока; буфер берёт, когда ожидание сказало, что есть что читать.
func Carry(dst io.Writer, src io.Reader, await func() bool) int64 {
	var total int64
	for await() {
		n, err := step(dst, src)
		total += n
		if err != nil {
			break
		}
	}
	return total
}

// Читает и пишет один буфер.
func step(dst io.Writer, src io.Reader) (int64, error) {
	buffer := buffers.Get().(*[]byte)
	held.Add(1)
	n, err := src.Read(*buffer)
	if n > 0 {
		if _, werr := dst.Write((*buffer)[:n]); werr != nil {
			n, err = 0, werr
		}
	}

	held.Add(-1)
	buffers.Put(buffer)
	return int64(n), err
}

// Ждёт байты или конец потока на сокете, не держа под них буфера; false, когда ждать больше нечего.
func AwaitSocket(conn net.Conn) bool {
	stream, ok := conn.(syscall.Conn)
	if !ok {
		return true
	}

	raw, err := stream.SyscallConn()
	if err != nil {
		return true
	}

	err = raw.Read(func(fd uintptr) bool {
		var peek [1]byte
		for {
			_, _, rerr := syscall.Recvfrom(int(fd), peek[:], syscall.MSG_PEEK|syscall.MSG_DONTWAIT)
			if rerr != syscall.EINTR {
				return rerr != syscall.EAGAIN
			}
		}
	})
	return err == nil
}
