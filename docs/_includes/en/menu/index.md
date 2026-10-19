# {{ page.title }}

The home screen covers connecting, the server and the routing list. Everything else is in the settings sections.

{% include children.html of="menu/index" %}

The "Settings" button on the home screen opens the settings. The sections stand on the left: "Configurations", "Routing", "Connections", "Diagnostics", "General"; "Geo sources" open from a link in the "Routing" section. The "Back" button above the sections leads back to the home screen.
{% if page.device == "android" %}
{% include todo.html text="a narrow screen: the list of sections and the open section take turns, the Back button of the phone; a TV and its remote" %}
{% endif %}
Changes in a section take effect after "Save"; until they are saved, the section says "Unsaved changes", and "Cancel" rolls them back. A setting that takes effect from the next connect shows "Saved. Reconnect to apply" after saving, with the "Reconnect" and "Later" buttons.
