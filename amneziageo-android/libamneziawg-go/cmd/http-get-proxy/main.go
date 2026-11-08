package main

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/signal"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/xhttpnative"
)

func main() {
	if len(os.Args) > 1 && os.Args[1] == "version" {
		fmt.Println("HTTP GET 1 experimental")
		return
	}
	data, err := io.ReadAll(io.LimitReader(os.Stdin, 32769))
	if err != nil || len(data) > 32768 {
		fmt.Fprintln(os.Stderr, "invalid HTTP GET profile")
		os.Exit(1)
	}
	xhttpnative.InstallProtector(func(int) bool { return true })
	host, err := xhttpnative.Start(data)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println("HTTP GET 1 started")
	ended := make(chan error, 1)
	go func() { ended <- host.Wait() }()
	interrupt := make(chan os.Signal, 1)
	signal.Notify(interrupt, os.Interrupt)
	select {
	case err = <-ended:
	case <-interrupt:
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if stop := host.Stop(ctx); err == nil {
		err = stop
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
