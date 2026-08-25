package main

import (
	"fmt"
	"io"
	"sync"
)

// Log of the gateway: one line per event on the pipe the parent reads, which puts them in the log of the
// application.
type lines struct {
	sync.Mutex
	out io.Writer
}

func newLog(out io.Writer) *lines {
	return &lines{out: out}
}

func (l *lines) write(level string, args ...any) {
	l.Lock()
	defer l.Unlock()
	fmt.Fprint(l.out, level, " ")
	fmt.Fprintln(l.out, args...)
}

func (l *lines) Trace(args ...any) {
	l.write("trace", args...)
}

func (l *lines) Debug(args ...any) {
	l.write("debug", args...)
}

func (l *lines) Info(args ...any) {
	l.write("info", args...)
}

func (l *lines) Warn(args ...any) {
	l.write("warn", args...)
}

func (l *lines) Error(args ...any) {
	l.write("error", args...)
}

func (l *lines) Fatal(args ...any) {
	l.write("error", args...)
}

func (l *lines) Panic(args ...any) {
	l.write("error", args...)
}
