#!/bin/sh
# Imports the configurations in /etc/amneziageo/configs and connects on the one AMNEZIAGEO_CONNECT names.
set -u

tries=0
until amneziageo status > /dev/null 2>&1; do
  tries=$((tries + 1))
  if [ "$tries" -ge 60 ]; then
    echo "amneziageo-bootstrap: the agent is not listening" >&2
    exit 1
  fi
  sleep 1
done

for file in /etc/amneziageo/configs/*.conf; do
  [ -f "$file" ] || continue
  name=$(basename "$file" .conf)
  if ! amneziageo config show "$name" > /dev/null 2>&1; then
    amneziageo config import "$name" --file "$file"
  fi
done

connect=${AMNEZIAGEO_CONNECT:-}
[ -n "$connect" ] || exit 0

status=$(amneziageo --json status)
if printf '%s' "$status" | grep -q '"surviveReboot": *true' \
  && printf '%s' "$status" | grep -q "\"selectedTarget\": *\"$connect\""; then
  exit 0
fi

amneziageo up "$connect" \
  && amneziageo settings set survive-reboot on \
  && amneziageo settings set periodic-reconnect-enabled on
