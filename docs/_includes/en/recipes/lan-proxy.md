# {{ page.title }}

{% include todo.html text="steps: turn the proxy on, allow connections from the LAN, add an account or allow password-free access, enter the address and port on the device - a TV, a console, a phone, Telegram" %}
{% if page.device == "android" %}

## Sharing from the phone

{% include todo.html text="a Wi-Fi hotspot, USB or Bluetooth tethering: on the joined device, enter the phone's address on the shared network and the HTTP port as the proxy of its Wi-Fi network" %}
{% endif %}

Proxy settings: [Connections](../menu/connections.html#proxy).
