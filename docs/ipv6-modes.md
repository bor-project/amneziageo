# IPv6 modes

Auto enables IPv6 inside the VPN when the operating system supports it and the configuration contains a usable IPv6 interface address and IPv6 routes. It does not require IPv6 on the external connection or verify IPv6 internet access beyond the VPN server. On retains the explicit transport setting; Off removes IPv6 addresses, routes and DNS from the generated tunnel configuration.

The Linux agent opens its SQLite store with `legacyIpv6Auto: true`. Initialization changes only null `config_transport.ipv6_mode` values to Auto, including databases that predate the column. Existing Auto, On and Off values stay unchanged. The previous `use_ipv6` value and other settings are preserved. Reading and importing legacy transport settings on Linux also select Auto when the new mode is absent. Other platform stores retain the legacy boolean interpretation.

Initialization is repeatable. After a user saves On or Off, later restarts and unrelated edits keep that choice. Bundles with an explicit mode keep it; legacy bundles imported on Linux receive Auto. Configurations without stored transport settings use Auto as before.

The settings status reports IPv6 availability for the selected configuration, including when On is selected. A change between Auto and an explicit mode requires reconnecting only when their effective transport settings differ; the mode itself is saved in either case.
