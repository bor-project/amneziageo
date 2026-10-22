#!/usr/bin/env bash
# Кадры окна Windows на стенде.
#   shoot.sh push               - положить набор на стенд и проверить, запущен ли исполнитель
#   shoot.sh <язык> [кадр ...]  - подготовить стенд, снять кадры (по умолчанию все из frames.txt), вернуть стенд
#   shoot.sh restore            - вернуть стенд, если съёмка оборвалась
#   shoot.sh clean              - остановить исполнитель и удалить набор со стенда
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
. "$here/local/stand.env"
host="$AG_HOST"
client="${AG_CLIENT:-C:/dev/ag/amneziageo}"
base="C:/Users/${host%@*}/AppData/Local/Temp/agshots"
winbase="${base//\//\\}"
lang=en
quiet() { grep -v "post-quantum\|store now\|openssh.com/pq\|server may need" || true; }
remote() { ssh -o BatchMode=yes "$host" "$1" < /dev/null 2>&1 | quiet | tr -d '\r'; }

push() {
  local strings="$here/jobs/strings" loc=amneziageo-lib/AmneziaGeo.Localization
  mkdir -p "$strings"
  for f in $(git -C "$client" ls-tree --name-only HEAD "$loc/" | grep -o 'Strings[.a-zA-Z-]*\.resx$'); do
    git -C "$client" show "HEAD:$loc/$f" > "$strings/$f"
  done
  remote "mkdir $winbase\\frames $winbase\\strings 2>nul & echo pushed to $winbase"
  scp -q -o BatchMode=yes "$here"/kit/* "$here/languages.psd1" "$here/local/mask.txt" "$host:$base/" 2>&1 | quiet
  if [ -f "$here/local/app.txt" ]; then
    scp -q -o BatchMode=yes "$here/local/app.txt" "$host:$base/" 2>&1 | quiet
  fi
  scp -q -o BatchMode=yes "$here"/frames/*.ps1 "$host:$base/frames/" 2>&1 | quiet
  scp -q -o BatchMode=yes "$strings"/*.resx "$host:$base/strings/" 2>&1 | quiet
  if ls "$here"/local/installer/*.exe > /dev/null 2>&1; then
    remote "mkdir $winbase\installer 2>nul & echo installer folder"
    scp -q -o BatchMode=yes "$here"/local/installer/*.exe "$host:$base/installer/" 2>&1 | quiet
  fi
}

alive() { remote "if exist $winbase\\runner.alive (echo up) else (echo down)" | grep -q '^up'; }

hint() {
  echo "Исполнитель не запущен. На стенде в сессии ${host%@*} (консоль, не ssh): Win+R и"
  echo "  wscript \"$winbase\\launch.vbs\""
}

job() { printf '%s\n' "\$Lang = '$lang'" ". (Join-Path \$base 'lib-fn.ps1')" "$3" | "$here/do.sh" "$1" "$2"; }

case "${1:-}" in
  push)
    push
    if alive; then echo "runner: up"; else hint; fi
    ;;
  restore)
    alive || { hint; exit 2; }
    job restore 300 ". (Join-Path \$base 'restore.ps1')"
    ;;
  clean)
    remote "type nul > $winbase\\stop & echo stop requested"
    sleep 3
    remote "rmdir /s /q $winbase & if exist $winbase (echo left) else (echo removed)"
    ;;
  ""|-h|--help)
    sed -n '2,6p' "$0"
    ;;
  *)
    lang="$1"; shift
    push
    alive || { hint; exit 2; }
    frames=("$@")
    if [ ${#frames[@]} -eq 0 ]; then
      mapfile -t frames < <(grep -v '^#' "$here/frames.txt" | awk 'NF { print $1 }')
    fi
    remote "if exist $winbase\\out\\$lang rmdir /s /q $winbase\\out\\$lang"
    job "setup-$lang" 300 ". (Join-Path \$base 'setup.ps1')"
    for f in "${frames[@]}"; do
      echo "== $f"
      job "$lang-$f" 300 ". (Join-Path \$base 'frames\\$f.ps1')"
    done
    job "restore-$lang" 300 ". (Join-Path \$base 'restore.ps1')"
    mkdir -p "$here/out/$lang"
    scp -q -o BatchMode=yes "$host:$base/out/$lang/*.png" "$here/out/$lang/" 2>&1 | quiet
    ls "$here/out/$lang"
    ;;
esac
