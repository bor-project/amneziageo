# {{ page.title }}
{% if page.device == "windows" %}

## Tunnel

{% include todo.html text="Show notifications, Reconnect after reboot, Reconnect automatically and Retry interval, Allow several VPNs" %}

How a connect is retried: [Reconnecting](home.html#reconnecting). Allow several VPNs turns on the mode of the [Multiple VPN connections](../recipes/multiple-vpn.html) recipe.
{% endif %}

## Proxy

The app runs SOCKS5 and HTTP proxies that let devices without their own client use the tunnel: a TV, a phone, a console.

- Ports are set separately for SOCKS5 and HTTP.
- Allowing connections from the LAN opens the proxy to the rest of the network; otherwise it serves this machine only.
- Access is by account, or password-free if that suits you better.
- Connected clients and the number of connections they hold are listed.
{% if page.device == "android" %}
The list also holds the phone's address on the network it shares itself: a Wi-Fi hotspot, USB or Bluetooth tethering. A device joined to that network takes this address and the HTTP port as the proxy in its Wi-Fi settings: without root Android keeps tethered traffic out of the VPN, and through the proxy it rides the tunnel. The address of the mobile network is not listed: it has no neighbours.
{% endif %}
A proxy set in the network settings is followed by the browser and by programs that reach the network through the system. Telegram on a phone is not one of them: the phone's address and the SOCKS5 port go into Telegram itself (Settings - Data and Storage - Proxy Settings, type SOCKS5). The proxy carries TCP connections; datagrams (UDP) of neighbouring devices do not pass through it.

Traffic reaches the tunnel only while the tunnel is up.

Step by step: the [Devices on the network through the proxy](../recipes/lan-proxy.html) recipe.
{% if page.device == "windows" or page.device == "linux" %}

## Access point

{% include todo.html text="the Wi-Fi tab: Access point, Network name, Password, Hidden network, Band, Devices" %}

Step by step: the [Sharing the VPN over Wi-Fi](../recipes/hotspot.html) recipe.
{% endif %}

## Subscription auto-refresh

{% include todo.html text="How often to refresh - applies when the panel sets no interval of its own" %}
