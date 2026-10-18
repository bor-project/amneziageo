#!/bin/bash
# Стенд кадров на машине Linux: запуск от своей учётки с sudo без пароля.
#   stand.sh build             собрать агент, консоль, окно (версия VERSION) и помощник в /var/tmp/agdocs
#   stand.sh start             поднять стенд: свои пространства, экран :97, агент на чистой библиотеке
#                              (KEEP=1 - на библиотеке и настройках окна прошлого подъёма)
#   stand.sh window <локаль>   (пере)запустить окно с помощником, например ru_RU.UTF-8
#   stand.sh stop              погасить всё, что работает в пространстве стенда
#   stand.sh in <команда...>   команда внутри стенда с DISPLAY=:97
#   stand.sh cli <аргументы>   консольный клиент против агента стенда
D=/var/tmp/agdocs
R=$D/run
BIN=$D/art/bin
KIT=$(cd "$(dirname "$0")" && pwd)
CLIENT=${CLIENT:-/home/bor/dev/amneziageo}
VERSION=${VERSION:-1.10.8.0}
UPDATE_URL=https://github.com/bor-project/amneziageo/releases/latest/download/update.json
export DOTNET_ROOT=/home/bor/.dotnet PATH=$PATH:/home/bor/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1

holder() { cat "$R/ns.pid" 2>/dev/null; }
inside() { sudo -n nsenter -t "$(holder)" -m -n -- env DISPLAY=:97 "$@"; }

# Процессы пространства стенда, кроме держателя: pid и строка запуска.
members() {
  local ns
  ns=$(sudo -n readlink "/proc/$(holder)/ns/net" 2>/dev/null) || return 0
  [ -n "$ns" ] || return 0
  for p in /proc/[0-9]*; do
    [ "${p#/proc/}" != "$(holder)" ] && [ "$(sudo -n readlink "$p/ns/net" 2>/dev/null)" = "$ns" ] \
      && echo "${p#/proc/} $(tr '\0' ' ' < "$p/cmdline" 2>/dev/null)"
  done
}

case "$1" in
  build)
    mkdir -p "$D" && chmod 700 "$D"
    git -C "$CLIENT" log -1 --format='client %h %s'
    for p in App Cli Ui; do
      dotnet build "$CLIENT/amneziageo-linux/AmneziaGeo.Linux.$p/AmneziaGeo.Linux.$p.csproj" -c Release \
        -p:Version="$VERSION" -p:UpdateUrl="$UPDATE_URL" --artifacts-path "$D/art" -v quiet --nologo -clp:ErrorsOnly < /dev/null || exit 1
    done
    dotnet build "$KIT/hook/AgShot.csproj" -c Release -p:AvaloniaDir="$BIN/AmneziaGeo.Linux.Ui/release" \
      -o "$D/hook" -v quiet --nologo -clp:ErrorsOnly < /dev/null || exit 1
    echo "built $VERSION"
    ;;
  start)
    [ -n "$(holder)" ] && sudo -n test -d "/proc/$(holder)" && { echo "already up: $(holder)"; exit 0; }
    mkdir -p "$R" && rm -f "$R/ns.pid" "$R/boot.err"
    setsid sudo -n env KEEP="${KEEP:-0}" unshare -m -n --propagation private bash "$KIT/boot.sh" < /dev/null > "$R/boot.log" 2>&1 &
    for _ in $(seq 1 80); do [ -s "$R/ns.pid" ] && break; [ -s "$R/boot.err" ] && { cat "$R/boot.err"; exit 1; }; sleep 0.5; done
    [ -s "$R/ns.pid" ] || { echo "stand did not come up"; cat "$R/boot.log"; exit 1; }
    echo "holder $(holder)"
    ;;
  window)
    locale=${2:-ru_RU.UTF-8}
    for pid in $(members | awk '/AmneziaGeo.Linux.Ui/ {print $1}'); do sudo -n kill "$pid"; done
    for _ in $(seq 1 20); do members | grep -q AmneziaGeo.Linux.Ui || break; sleep 0.25; done
    rm -f "$R/hook/req" "$R/hook/res"
    sudo -n rm -f "$R/ui.log"
    setsid sudo -n nsenter -t "$(holder)" -m -n -- runuser -u bor -- env DISPLAY=:97 DOTNET_ROOT=/home/bor/.dotnet \
      HOME="$R/home" XDG_RUNTIME_DIR="$R/xdg" LANG="$locale" LC_ALL="$locale" LANGUAGE= AGSHOT_DIR="$R/hook" \
      DOTNET_STARTUP_HOOKS="$D/hook/AgShot.dll" "$BIN/AmneziaGeo.Linux.Ui/release/AmneziaGeo.Linux.Ui" \
      < /dev/null > "$R/ui.log" 2>&1 &
    echo "window $locale"
    ;;
  members)
    members
    ;;
  stop)
    pid=$(holder)
    [ -n "$pid" ] || exit 0
    for p in $(members | awk '{print $1}'); do sudo -n kill "$p" 2>/dev/null; done
    sleep 2
    sudo -n kill "$pid" 2>/dev/null
    rm -f "$R/ns.pid"
    echo "stopped"
    ;;
  in)
    shift
    inside "$@"
    ;;
  cli)
    shift
    sudo -n nsenter -t "$(holder)" -m -n -- env DOTNET_ROOT=/home/bor/.dotnet "$BIN/AmneziaGeo.Linux.Cli/release/amneziageo" "$@" < /dev/null
    ;;
  *)
    sed -n '2,9p' "$0"
    exit 1
    ;;
esac
