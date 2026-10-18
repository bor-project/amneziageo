#!/bin/bash
# Стенд кадров в своих пространствах: экран, оконный менеджер и агент на чистой библиотеке; окно поднимает
# stand.sh window. Запуск от root под unshare -m -n (stand.sh start); держит пространства, пока его не погасят.
D=/var/tmp/agdocs
R=$D/run
BIN=$D/art/bin

# Хост не виден: свой /tmp (сокет агента), свой /run (системная шина, сеанс владельца, сокеты движка хоста),
# свой sysfs (интерфейсы этого пространства; cgroup хоста агенту не достаётся).
mount -t tmpfs tmpfs /tmp
mount -t tmpfs tmpfs /run
mount -t sysfs sysfs /sys
printf 'nameserver 1.1.1.1\n' > /tmp/resolv.conf
mount --bind /tmp/resolv.conf /etc/resolv.conf
# Агент удаляет и пишет /etc/resolv.conf заново: без своей точки монтирования он тронул бы файл хоста.
mountpoint -q /etc/resolv.conf || { echo "resolv.conf is not mounted over" > "$R/boot.err"; exit 1; }
ip link set lo up

[ "${KEEP:-0}" = 1 ] || rm -rf "$R/data" "$R/home"
rm -rf "$R/xdg" "$R/hook"
mkdir -p "$R/data" "$R/home/.local/share" "$R/xdg" "$R/hook"
chown -R bor:bor "$R/home" "$R/xdg" "$R/hook"
chmod 700 "$R/xdg"

# Библиотека агента по обычному пути пакета: её путь виден в диагностике. Своя папка поверх пути хоста видна только
# в пространстве стенда; без привязки агент писал бы в библиотеку хоста.
LIBRARY=$R/data
if [ -d /var/lib/amneziageo ]; then
  mount --bind "$R/data" /var/lib/amneziageo
  mountpoint -q /var/lib/amneziageo || { echo "library is not mounted over" > "$R/boot.err"; exit 1; }
  LIBRARY=/var/lib/amneziageo
fi
export DOTNET_ROOT=/home/bor/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 AMNEZIAGEO_DATA=$LIBRARY
Xvfb :97 -screen 0 1600x1000x24 -nolisten tcp > "$R/xvfb.log" 2>&1 &
for _ in $(seq 1 40); do [ -S /tmp/.X11-unix/X97 ] && break; sleep 0.25; done
DISPLAY=:97 openbox --sm-disable > "$R/openbox.log" 2>&1 &

cd "$BIN/AmneziaGeo.Linux.App/release" || exit 1
./AmneziaGeo.Linux.App --iface agd0 > "$R/agent.log" 2>&1 &
for _ in $(seq 1 60); do [ -S /tmp/CoreFxPipe_AmneziaGeo.Agent ] && break; sleep 0.5; done
echo $$ > "$R/ns.pid"
sleep infinity
