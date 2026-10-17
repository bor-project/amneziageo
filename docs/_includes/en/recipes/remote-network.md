# {{ page.title }}

Subnets behind the server are reachable in two ways:

- the full tunnel - everything goes to the other side, including its internal addresses;
- an address-range rule in split mode, for example `cidr:10.8.0.0/24` - only that subnet takes the tunnel.

The DNS from the configuration is applied, so internal names resolve.

If the subnet on the other side matches your local one - both `192.168.1.0/24` - no route to it can be built; only re-addressing one of the ends helps.

More: [Modes](../menu/routing.html#modes), [Rule lists](../menu/routing.html#rule-lists).
