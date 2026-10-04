/* SPDX-License-Identifier: MIT
 *
 * Tests of the stream copy that holds no buffer while it waits (AmneziaGeo).
 */

package pump

import (
	"bytes"
	"io"
	"net"
	"testing"
	"time"
)

// Два соединённых сокета на петле.
func pair(t *testing.T) (net.Conn, net.Conn) {
	t.Helper()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()

	near, err := net.Dial("tcp4", listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}

	far, err := listener.Accept()
	if err != nil {
		t.Fatal(err)
	}

	t.Cleanup(func() {
		near.Close()
		far.Close()
	})
	return near, far
}

// Ждёт, пока все буферы вернутся; отвечает, сколько осталось на руках.
func settled() int64 {
	for attempt := 0; attempt < 750 && Held() > 0; attempt++ {
		time.Sleep(20 * time.Millisecond)
	}
	return Held()
}

func TestBytesGoAcrossUntilTheStreamEnds(t *testing.T) {
	source, near := pair(t)
	far, sink := pair(t)
	sent := bytes.Repeat([]byte("0123456789abcdef"), 40000)
	carried := make(chan int64, 1)
	go func() {
		carried <- Carry(far, near, func() bool { return AwaitSocket(near) })
	}()

	received := make(chan []byte, 1)
	go func() {
		data, _ := io.ReadAll(io.LimitReader(sink, int64(len(sent))))
		received <- data
	}()

	if _, err := source.Write(sent); err != nil {
		t.Fatal(err)
	}
	source.(*net.TCPConn).CloseWrite()

	if got := <-received; !bytes.Equal(got, sent) {
		t.Fatalf("%d bytes came across of %d", len(got), len(sent))
	}

	select {
	case total := <-carried:
		if total != int64(len(sent)) {
			t.Fatalf("%d bytes were counted of %d", total, len(sent))
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the copy did not end with the stream")
	}

	if left := settled(); left != 0 {
		t.Fatalf("%d buffer(s) are held after the stream ended", left)
	}
}

func TestAStreamThatWaitsHoldsNoBuffer(t *testing.T) {
	source, near := pair(t)
	far, sink := pair(t)
	done := make(chan struct{})
	go func() {
		Carry(far, near, func() bool { return AwaitSocket(near) })
		close(done)
	}()

	if _, err := source.Write(make([]byte, 2000)); err != nil {
		t.Fatal(err)
	}

	if _, err := io.ReadFull(sink, make([]byte, 2000)); err != nil {
		t.Fatal(err)
	}

	if left := settled(); left != 0 {
		t.Fatalf("%d buffer(s) are held by a stream that waits", left)
	}

	source.Close()
	select {
	case <-done:
	case <-time.After(10 * time.Second):
		t.Fatal("the copy did not end with the stream")
	}
}

func TestAClosedSocketEndsTheWait(t *testing.T) {
	_, near := pair(t)
	near.Close()

	if AwaitSocket(near) {
		t.Fatal("a closed socket was waited on")
	}
}
