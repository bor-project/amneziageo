# {{ page.title }}

Windows can keep several VPN tunnels connected at the same time. In the settings, turn on **Allow several VPNs**, then connect the required server configurations. Connecting another server adds a tunnel while the existing ones stay connected.

- Choose a **main** server and the order of the **reserve** servers. A **neutral** server stays outside that reserve order and carries traffic explicitly assigned to it.
- A routing rule can use **Auto**, **Best**, **Direct**, **Block**, or a named server. You can also select a fallback for that rule.
- This combines split tunneling with several connections: for example, send a work subnet through the office VPN and selected sites through a different VPN, leaving the remaining traffic direct in split-tunnel mode.

This mode is currently Windows-only. Linux and Android keep one active VPN connection. See the [command-line examples](../cli.html#multiple-vpn-connections).

## Step by step in the window

1. In the "Connections" section, on the "Tunnel" tab, turn "Allow several VPNs" on.

   {% include shot.html name="connections-multi" alt="The Tunnel tab: Allow several VPNs is on, under it the check interval, the checks before a switch and the threshold" caption="Several VPNs and how rules move between servers." %}
2. In the configuration catalogue turn "In use" on for every server that should be up: each adds its own tunnel, and the ones running stay.
3. On the home screen, in the "Servers" block, click the place of a server and pick "Primary", "Reserve 1", "Reserve 2" and on, or "Neutral".

   {% include shot.html name="home-servers" alt="Home screen in the multiple-VPN mode: the Servers block, two servers up and their places" caption="The Servers block on the home screen." %}
4. In the list editor, on a "Through VPN" entry, pick the "Route" - "Auto", "Best", "Direct", "Block" or a server - and the "Fallback": where the traffic goes when the chosen server is silent.

   {% include shot.html name="routing-route" alt="The list editor in the multiple-VPN mode: an unfolded entry with Route and Fallback" caption="Route and fallback on the entries of a list." %}

How rules move between servers is set under the "Allow several VPNs" switch: "Check interval", "Checks before a switch", "Switch threshold". When the server the tunnel ran on is gone, the tunnel moves to another one, and the window says so.
