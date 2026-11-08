module github.com/bor-project/amneziageo/libamneziawg-go

go 1.27.3

require (
	github.com/amnezia-vpn/amneziawg-go/v3 v3.1.20260828
	gvisor.dev/gvisor v0.0.0-20260122175437-89a5d21be8f0
)

require (
	github.com/google/btree v1.1.3 // indirect
	golang.org/x/crypto v0.58.0 // indirect
	golang.org/x/exp v0.0.0-20240506185415-9bf2ced13842 // indirect
	golang.org/x/net v0.61.0 // indirect
	golang.org/x/sys v0.49.0 // indirect
	golang.org/x/time v0.14.0 // indirect
	golang.zx2c4.com/wintun v0.0.0-20230126152724-0fa3db229ce2 // indirect
)

replace github.com/amnezia-vpn/amneziawg-go/v3 => ../amneziawg-go
