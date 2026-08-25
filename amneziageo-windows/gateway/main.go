// Gateway of the shared access point: an adapter of its own, a userspace stack on it, and every connection the
// clients open handed to the local proxy of this machine, which reopens it under the rules this machine goes
// out under.
package main

import (
	"context"
	"flag"
	"fmt"
	"net/netip"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	tun "github.com/sagernet/sing-tun"
	"github.com/sagernet/sing/common/control"

	"golang.org/x/sys/windows"
)

const (
	udpTimeout  = 5 * time.Minute
	icmpTimeout = 30 * time.Second
)

func main() {
	name := flag.String("name", "AmneziaGeo Gateway", "name of the adapter to raise")
	address := flag.String("address", "", "address the adapter carries, with prefix")
	routes := flag.String("routes", "", "prefixes routed into the adapter, comma separated")
	dns := flag.String("dns", "", "resolver the adapter hands out")
	proxy := flag.String("proxy", "", "address of the local proxy every connection goes to")
	mtu := flag.Uint("mtu", 1420, "how much the adapter carries in one packet")
	parent := flag.Int("parent", 0, "process this one does not outlive")
	flag.Parse()

	if err := run(*name, *address, *routes, *dns, *proxy, uint32(*mtu), *parent); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(name, address, routes, dns, proxy string, mtu uint32, parent int) error {
	if proxy == "" {
		return fmt.Errorf("no local proxy given")
	}

	options, err := adapter(name, address, routes, dns, mtu)
	if err != nil {
		return err
	}

	log := newLog(os.Stdout)
	options.Logger = log

	finder := control.NewDefaultInterfaceFinder()
	options.InterfaceFinder = finder

	network, err := tun.NewNetworkUpdateMonitor(log)
	if err != nil {
		return fmt.Errorf("watch the adapters: %w", err)
	}

	defer network.Close()

	monitor, err := tun.NewDefaultInterfaceMonitor(network, log, tun.DefaultInterfaceMonitorOptions{InterfaceFinder: finder})
	if err != nil {
		return fmt.Errorf("watch the default route: %w", err)
	}

	defer monitor.Close()

	options.InterfaceMonitor = monitor
	if err = network.Start(); err != nil {
		return fmt.Errorf("watch the adapters: %w", err)
	}

	if err = monitor.Start(); err != nil {
		return fmt.Errorf("watch the default route: %w", err)
	}

	device, err := tun.New(options)
	if err != nil {
		return fmt.Errorf("raise the adapter: %w", err)
	}

	defer device.Close()

	if err = device.Start(); err != nil {
		return fmt.Errorf("start the adapter: %w", err)
	}

	ctx, stop := context.WithCancel(context.Background())
	defer stop()

	stack, err := tun.NewStack("gvisor", tun.StackOptions{
		Context:         ctx,
		Tun:             device,
		TunOptions:      options,
		UDPTimeout:      udpTimeout,
		ICMPTimeout:     icmpTimeout,
		Handler:         &handler{proxy: proxy, log: log},
		Logger:          log,
		InterfaceFinder: finder,
	})
	if err != nil {
		return fmt.Errorf("build the stack: %w", err)
	}

	if err = stack.Start(); err != nil {
		return fmt.Errorf("start the stack: %w", err)
	}

	defer stack.Close()

	log.Info("gateway: ", name, " ", address, " to ", proxy)
	go interrupted(stop)
	go orphaned(parent, stop)
	<-ctx.Done()
	log.Info("gateway: down")
	return nil
}

// The adapter the clients are routed into: the address it answers on, the prefixes that reach it, and the
// resolver it hands to whoever asks it for one.
func adapter(name, address, routes, dns string, mtu uint32) (tun.Options, error) {
	prefix, err := netip.ParsePrefix(address)
	if err != nil {
		return tun.Options{}, fmt.Errorf("address %q: %w", address, err)
	}

	options := tun.Options{
		Name:         name,
		MTU:          mtu,
		Inet4Address: []netip.Prefix{prefix},
		AutoRoute:    true,
		// Without this the adapter hands out a resolver of its own, and a query sent to it comes back through
		// the adapter and round again.
		EXP_DisableDNSHijack: true,
	}

	for _, item := range strings.Split(routes, ",") {
		item = strings.TrimSpace(item)
		if item == "" {
			continue
		}

		route, routeErr := netip.ParsePrefix(item)
		if routeErr != nil {
			return tun.Options{}, fmt.Errorf("route %q: %w", item, routeErr)
		}

		options.Inet4RouteAddress = append(options.Inet4RouteAddress, route)
	}

	if dns != "" {
		resolver, dnsErr := netip.ParseAddr(dns)
		if dnsErr != nil {
			return tun.Options{}, fmt.Errorf("resolver %q: %w", dns, dnsErr)
		}

		options.DNSServers = []netip.Addr{resolver}
	}

	return options, nil
}

func interrupted(stop context.CancelFunc) {
	signals := make(chan os.Signal, 1)
	signal.Notify(signals, os.Interrupt, syscall.SIGTERM)
	<-signals
	stop()
}

// Follows the agent down: an adapter left standing after it would carry the clients out with nothing behind it.
func orphaned(parent int, stop context.CancelFunc) {
	if parent <= 0 {
		return
	}

	handle, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(parent))
	if err != nil {
		return
	}

	defer windows.CloseHandle(handle)
	windows.WaitForSingleObject(handle, windows.INFINITE)
	stop()
}
