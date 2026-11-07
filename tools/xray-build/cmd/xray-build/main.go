package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	"amneziageo.local/xrayruntime"
)

func main() {
	cache := flag.String("cache", "", "runtime cache directory")
	system := flag.String("os", "", "target OS")
	architecture := flag.String("arch", "", "target architecture")
	compiler := flag.String("go", "go", "Go1.27.3 executable")
	flag.Parse()
	if *cache == "" {
		fmt.Fprintln(os.Stderr, "runtime cache directory is required")
		os.Exit(1)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Minute)
	defer cancel()
	directory, err := xrayruntime.Build(ctx, *cache, *system, *architecture, *compiler)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println(directory)
}
