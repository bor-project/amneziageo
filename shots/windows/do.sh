#!/usr/bin/env bash
# do.sh <задание> [секунд] < сценарий.ps1 - выполняет сценарий в сессии рабочего стола стенда и печатает вывод.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
. "$here/local/stand.env"
name="$1"
wait="${2:-120}"
host="$AG_HOST"
base="C:/Users/${host%@*}/AppData/Local/Temp/agshots"
winbase="${base//\//\\}"
mkdir -p "$here/jobs"
cat > "$here/jobs/$name.ps1"
quiet() { grep -v "post-quantum\|store now\|openssh.com/pq\|server may need"; }
scp -q -o BatchMode=yes "$here/jobs/$name.ps1" "$host:$base/inbox/$name.part" 2>&1 | quiet
ssh -o BatchMode=yes "$host" "move /y $winbase\\inbox\\$name.part $winbase\\inbox\\$name.ps1 > nul" < /dev/null 2>&1 | quiet
for _ in $(seq "$wait"); do
  if ssh -o BatchMode=yes "$host" "if exist $winbase\\outbox\\$name.txt.done (type $winbase\\outbox\\$name.txt & del $winbase\\outbox\\$name.txt.done $winbase\\outbox\\$name.txt & echo @@DONE)" < /dev/null 2>&1 | quiet > "$here/jobs/$name.out"; grep -q "@@DONE" "$here/jobs/$name.out"; then
    sed '/@@DONE/d' "$here/jobs/$name.out" | sed 's/\r$//'
    exit 0
  fi
  sleep 1
done
echo "timeout waiting for $name"
exit 1
