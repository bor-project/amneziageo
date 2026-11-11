package main

import (
	"fmt"
	"os"
)

func main() {
	value, err := sourceFingerprint()
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println(value)
}
