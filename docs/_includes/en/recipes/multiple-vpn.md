# {{ page.title }}

Windows can keep several VPN tunnels connected at the same time. In the settings, enable the multiple-VPN option, then connect the required server configurations. Connecting another server adds a tunnel while the existing ones stay connected.

- Choose a **main** server and the order of the **reserve** servers. A **neutral** server stays outside that reserve order and carries traffic explicitly assigned to it.
- A routing rule can use **Auto**, **Best**, **Direct**, **Block**, or a named server. You can also select a fallback for that rule.
- This combines split tunneling with several connections: for example, send a work subnet through the office VPN and selected sites through a different VPN, leaving the remaining traffic direct in split-tunnel mode.

This mode is currently Windows-only. Linux and Android keep one active VPN connection. See the [command-line examples](../cli.html#multiple-vpn-connections).

{% include todo.html text="step by step in the window: Allow several VPNs in Connections, server roles on the home screen and in the catalogue, the server and fallback of a rule" %}
