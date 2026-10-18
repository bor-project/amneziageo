# {{ page.title }}

{% include todo.html text="the connect button and its states; Server - the configuration pick; Routing - the switch and the list name that leads into the section; Add configuration while there are none" %}
{% if page.device == "windows" %}

## Servers

{% include todo.html text="the Servers block in the multiple-VPN mode: the servers up, the place of each, latency, speed and loss, More - into the catalogue" %}

The mode as a whole: the [Multiple VPN connections](../recipes/multiple-vpn.html) recipe.
{% endif %}
{% if page.device == "android" %}

## Always-on VPN

{% include todo.html text="“Always-on VPN is on: the button opens the system settings”; a TV has no such switch" %}
{% endif %}

## Holding traffic when the tunnel drops

While the switch "Hold traffic when the tunnel drops" in the settings of a configuration is on, a tunnel whose server stops answering is not taken down: it stays up while it reconnects on its own, what the rules send through it is held back instead of leaving by the physical path, and what the rules send directly keeps going. The state reads "connecting" until the server answers again, however long that takes. The switch is on by default. With it off such a tunnel is taken down and raised again, and while it is down what the rules send through it leaves by the physical path. From the console: `amneziageo config hold <name> on|off`; `config list` shows it in the HOLD column and `status` in the `hold traffic` row.
{% if page.device == "android" %}
A switch flipped while the tunnel is connected applies from the next connect.
{% else %}
A switch flipped while the tunnel is connected applies at once.
{% endif %}

- A disconnect of yours (the button, the menu of the tray or of the notification, the console) takes the tunnel down, and so does a switch to another configuration on Linux and Windows.
- An AmneziaGeo server takes the tunnel down as well when the client is turned off in its panel: it sends a signal through the tunnel, and the application disconnects the way a disconnect of yours does, so a machine that was reached through the tunnel comes back on its own network. On Windows with several servers only the tunnel of that server goes down, and the rest of the set stands. The application takes the signal on Linux, Windows and Android, on a TCP port the server names, at the address of the tunnel; on Linux and Windows it lets that port in from the server alone, whatever the access from the tunnel says. On Linux a firewall of the machine itself that drops incoming connections drops the signal too.
- The signal is taken while the configuration lets the server take the tunnel down, and by default it does. The switch "Allow the server to disconnect the tunnel" stands in the settings of a configuration whose server sends the signal and leaves the choice to its client; in the console it is `amneziageo config signal <name> on|off`, and `config list` prints the state in the SIGNAL column. With the switch off the application does not listen for the signal and says so to the server the next time it asks it, and the server sends none. The change stands from the next connect. On Windows with several servers the switch of each configuration stands for its own tunnel alone: a signal one configuration takes leaves the tunnels of the others up.
- The protection covers a tunnel the server answered at least once after you connected. A connect the server never answered is given up.
- A tunnel whose engine or tunnel service died is raised again at once, and for that moment there is no tunnel. On Android the system setting "Block connections without VPN" closes it.
{%- if page.device == "android" %}
- The protection keeps the tunnel interface in place for as long as the session runs: an address the rules send directly keeps going through the application until the next connect instead of leaving the interface on its own. A change of the routing list or of the configuration still puts another interface in place, and for that moment a packet can leave by the physical path.
- Names are resolved through the tunnel, so while it is held no name resolves; addresses the rules send directly keep working.
{%- endif %}
- A failure another attempt does not get past ends the hold: the tunnel goes down and says why.
{%- if page.device != "android" %}
- A tunnel that carries everything does not look the name of its server up again while it is held.
{%- endif %}

## Reconnecting
{% if page.device == "windows" %}
A dropped or failed connect is dialled again whatever auto reconnect says: at once, then in 5, 10 and 20 s, then every 60 s. With the setting on, the longest pause is its interval instead of 60 s, and `amneziageo status` says `always` either way. The setting is on the [Tunnel](connections.html#tunnel) tab of Connections.
{% elsif page.device == "linux" %}
A dropped tunnel or a failed connect is dialled again 30 s later (`periodic-reconnect-interval-seconds`), and so on until it connects, while auto reconnect is on; it is on unless you turn it off. A tunnel whose server falls silent is repaired in place first, then raised again, and taken down when the server does not answer the new session within 30 s. Between attempts there is no tunnel, so traffic leaves by the physical path unless the leak protection holds a tunnel that stood.
{% elsif page.device == "android" %}
A connect stays dialled until the tunnel is up or you take it back: with no network it waits for one, and after a failure it tries again at once, then in 5, 10 and 20 s, then every 60 s. Between attempts there is no tunnel, so traffic leaves by the physical path unless the system setting "Block connections without VPN" is on or the leak protection holds a tunnel that stood.
{% endif %}
{% if page.device == "android" %}

## Switching the configuration

A connect to another configuration while a tunnel is connected asks the server of that configuration for a handshake first, past the tunnel. A server that does not answer within 7 s is not switched to: the tunnel stays on the configuration it was on, and the window names the server that did not answer and the configuration the tunnel stays on.

## When the system stops the tunnel

The application follows the tunnel process: when the system kills it and nobody asked for a disconnect, a window that is on the screen raises the tunnel again within a few seconds. With no window on the screen the tunnel raises itself: while a session is wanted the service keeps pushing an alarm of the system ahead, and once the process is gone the alarm starts it again within a few minutes. A tunnel the alarm has raised three times in a row without a connection stays down, and a "The tunnel has stopped" notification with a "Connect" button says so; that notification has a category of its own, so its sound can be set apart from the rest.

## The tunnel notification

The notification of the tunnel names the configuration and shows the stage with the speed in both directions, refreshed every 5 seconds while the screen is on, and the attempt while the tunnel is dialled again; unfolded it names the routing list in use. Its button takes the tunnel down, and a tap opens the application. A tunnel you took down leaves a "Disconnected" notification with a "Connect" button, which can be swiped away.
{% endif %}
