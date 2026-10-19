# {{ page.title }}

The databases for `geosite:` rules - domain categories - and `geoip:` rules - countries and networks. The section opens from a link in [Routing](routing.html).

## Default databases

A fresh installation gets its sources by itself: [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat) (`geosite` and `geoip`), [runetfreedom](https://github.com/runetfreedom/russia-blocked-geosite) (`geosite-ru-only` and `geoip-ru-only`), [zkeen-ip](https://github.com/jameszeroX/zkeen-ip) (`geoip`) and the project's own databases [amneziageo-geo](https://github.com/bor-project/amneziageo-geo) (`geoip` and `geosite`; their keys start with `ag-`, so they do not cover the keys of other databases). Copies of the runetfreedom databases come with the application, so rules on them work before the first download. The other databases are fetched with "Update all" or with the "Update now" button on the home screen when the check found what is missing{% if page.device == "windows" %}; the Windows installer offers to fetch them during the installation{% elsif page.device == "linux" %}; from the console it is `sudo amneziageo geo download`{% endif %}. A source added to the default set later appears with an update of the application; one you removed does not come back.

## Database list

Each database is a row: the name, the type, the number of categories and the time of the download, "not loaded" or "error" when there is no file, and the megabytes received while it downloads. Databases of one type stack in order: a category is taken from the last database in the list that has it, so a database of your own adds to the common ones instead of replacing them. A right click or ⋮ on a row: "Update", "Edit" the address or the type, "Delete". "Update all" fetches again the databases whose files changed: the server is asked with the ETag and the date of the file, and a file that did not change is not downloaded.
{% if page.device == "windows" %}

{% include shot.html name="geo-sources" alt="Geo sources: auto-update, adding a database and the list of sources" caption="Geo sources: auto-update, a new database, the database list." %}
{% endif %}

## Add a database

Paste a direct link to a `geosite.dat` or `geoip.dat` file and press "Add". The type is taken from the file name in the link when it has `geosite` or `geoip` in it; otherwise pick it yourself. The link has to lead to the file itself (raw), not to the page of a repository: a page is not read as a database, and the application says so. An added database is downloaded at once.

## Auto-update

The "Auto-update" switch checks once a day whether new versions of the databases are out, without downloading them: by the `.sha256sum` checksum published beside the file, and where there is none, by the headers and the size of the file. A database with a new version is marked "Update available", and the home screen shows "Geo list updates available": "Update now" fetches them, "Later" hides the bar until the next check. On by default.

An AmneziaGeo server sends its sources with the answer for a configuration, see [What the server offers](configurations.html#what-the-server-offers).

## From the console

```bash
amneziageo source list
amneziageo source add geosite|geoip <url>
amneziageo source edit <name> geosite|geoip <url>
amneziageo source remove <name>
amneziageo geo list --filter telegram   # categories of the loaded databases
amneziageo geo show geosite:telegram    # what a category holds
amneziageo geo check                    # whether new versions are out, without downloading
amneziageo geo update [<source>]        # download again
amneziageo geo download                 # add the default sources and download them
```
