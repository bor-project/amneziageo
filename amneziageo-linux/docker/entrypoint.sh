#!/bin/sh
# Starts the agent and hands it the configurations laid into the container.
set -eu

( amneziageo-bootstrap & )
exec /opt/amneziageo/AmneziaGeo.Linux.App --iface "${AMNEZIAGEO_IFACE:-awg0}" "$@"
