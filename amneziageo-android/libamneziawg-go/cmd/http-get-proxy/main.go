package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/signal"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/transportnative"
	"github.com/bor-project/amneziageo/libamneziawg-go/xhttpnative"
)

func main() {
	if len(os.Args) == 2 && os.Args[1] == "version" {
		fmt.Println("HTTP GET 1 transports-v2 GET POST QUIC")
		return
	}
	probe := len(os.Args) == 2 && os.Args[1] == "probe"
	if len(os.Args) != 1 && !probe {
		fmt.Fprintln(os.Stderr, "PROXY 2 failed bad-proxy")
		os.Exit(1)
	}
	data, err := io.ReadAll(io.LimitReader(os.Stdin, 32769))
	var envelope struct {
		Version json.RawMessage `json:"version"`
	}
	if err != nil || len(data) > 32768 || json.Unmarshal(data, &envelope) != nil {
		fmt.Fprintln(os.Stderr, "PROXY 2 failed bad-proxy")
		os.Exit(1)
	}
	modern := len(envelope.Version) != 0
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	xhttpnative.InstallProtector(func(int) bool { return true })
	err = run(ctx, data, modern, probe)
	cancel()
	if err != nil {
		fmt.Fprintln(os.Stderr, "PROXY 2 failed "+transportnative.FaultCode(err))
		os.Exit(1)
	}
}

func run(ctx context.Context, data []byte, modern, probe bool) error {
	if probe {
		deadline, cancel := context.WithTimeout(ctx, 10*time.Second)
		defer cancel()
		if err := transportnative.Probe(deadline, data); err != nil {
			return err
		}
		fmt.Println("PROXY 2 available")
		return nil
	}
	var host transportnative.Carrier
	var err error
	if modern {
		host, err = transportnative.Start(data, nil)
	} else {
		host, err = xhttpnative.Start(data)
	}
	if err != nil {
		return err
	}
	defer func() {
		deadline, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		host.Stop(deadline)
	}()
	if modern {
		deadline, cancel := context.WithTimeout(ctx, 60*time.Second)
		err = host.WaitReady(deadline)
		cancel()
		if err != nil {
			return err
		}
		fmt.Println("PROXY 2 ready")
	} else {
		fmt.Println("HTTP GET 1 started")
	}
	ended := make(chan error, 1)
	go func() { ended <- host.Wait() }()
	select {
	case err = <-ended:
		return err
	case <-ctx.Done():
		return nil
	}
}
