#!/usr/bin/env bash
# Builds the documentation site the way the Pages workflow does and serves it on 127.0.0.1, rebuilding on save.
#   docs-site.sh serve | wait | stop | build | check
# AG_DOCS_HOME holds the gems (Gemfile with github-pages and webrick, BUNDLE_PATH vendor/bundle) and the built site.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
HOME_DIR="${AG_DOCS_HOME:-$HOME/ag-docs}"
PORT="${AG_DOCS_PORT:-4000}"
URL="http://127.0.0.1:$PORT/amneziageo/"
SOURCES=("$ROOT/docs" "$ROOT/CODE_SIGNING.md" "$ROOT/PRIVACY.md" "$ROOT/.github/workflows/pages.yml")
STAMP="$HOME_DIR/.built"
PIDFILE="$HOME_DIR/serve.pid"
READY="$HOME_DIR/ready"

need_gems() {
  if [ ! -f "$HOME_DIR/Gemfile" ]; then
    echo "No Gemfile in $HOME_DIR: put gems github-pages and webrick there, then bundle config set path vendor/bundle; bundle install"
    exit 1
  fi
  if command -v ruby > /dev/null; then
    PATH="$(ruby -e 'print Gem.user_dir')/bin:$PATH"
  fi
}

build() (
  set -e
  local work="$HOME_DIR/work" next="$HOME_DIR/work.next"
  rm -rf "$next"
  mkdir -p "$next"
  cp -R "$ROOT/docs" "$ROOT/CODE_SIGNING.md" "$ROOT/PRIVACY.md" "$next/"
  find "$next" -type f \( -name '*.md' -o -name '*.yml' -o -name '*.html' -o -name '*.css' \) -exec sed -i 's/\r$//' {} +
  python3 - "$ROOT/.github/workflows/pages.yml" > "$next/prepare.sh" <<'EOF'
import re
import sys
text = open(sys.argv[1], encoding="utf-8").read().replace("\r", "")
match = re.search(r"- name: Prepare documentation\n(?:.*\n)*?\s+run: \|\n((?:          .*\n|\n)+)", text)
print("\n".join(line[10:] for line in match.group(1).split("\n")))
EOF
  (cd "$next" && bash prepare.sh)
  cd "$HOME_DIR"
  PAGES_REPO_NWO=bor-project/amneziageo JEKYLL_ENV=production \
    bundle exec github-pages build --source "$next/_pages_source" --destination "$next/_site" 2>&1 | tee "$next/build.log"
  rm -rf "$HOME_DIR/work.old"
  if [ -d "$work" ]; then
    mv "$work" "$HOME_DIR/work.old"
  fi
  mv "$next" "$work"
  rm -rf "$HOME_DIR/work.old"
  echo "pages: $(find "$work/_site" -name '*.html' | wc -l)"
)

stop() {
  [ -f "$PIDFILE" ] || return 0
  local pid
  pid="$(cat "$PIDFILE")"
  if grep -qs docs-site.sh "/proc/$pid/cmdline"; then
    pkill -TERM -P "$pid"
    kill -TERM "$pid" 2>/dev/null
    for _ in $(seq 50); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
  fi
  rm -f "$PIDFILE" "$READY"
}

wait_ready() {
  for _ in $(seq 600); do
    if [ -f "$PIDFILE" ] && [ "$READY" -nt "$PIDFILE" ]; then
      return 0
    fi
    sleep 0.2
  done
  echo "The site was not built in 2 minutes, log: $HOME_DIR/build.out"
  return 1
}

rebuild() {
  echo "$(date +%T) building"
  touch "$STAMP"
  if bash "$HERE/docs-site.sh" build > "$HOME_DIR/build.out" 2>&1; then
    echo "$(date +%T) built $URL"
  else
    tail -20 "$HOME_DIR/build.out"
    echo "$(date +%T) failed $URL (log $HOME_DIR/build.out)"
  fi
  touch "$READY"
}

serve() {
  need_gems
  stop
  if ss -ltn "sport = :$PORT" | grep -q LISTEN; then
    echo "Port $PORT is taken by another process"
    exit 1
  fi
  mkdir -p "$HOME_DIR/srv"
  ln -sfn "$HOME_DIR/work/_site" "$HOME_DIR/srv/amneziageo"
  python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$HOME_DIR/srv" > /dev/null 2>&1 &
  server=$!
  echo "$$" > "$PIDFILE"
  trap 'kill "$server" 2>/dev/null; rm -f "$PIDFILE" "$READY"' EXIT
  trap 'exit 0' INT TERM HUP
  for _ in $(seq 50); do
    ss -ltn "sport = :$PORT" | grep -q LISTEN && break
    sleep 0.1
  done
  echo "Site: $URL from $ROOT - rebuilt on save, Ctrl+C stops"
  rebuild
  while sleep 2; do
    if [ -n "$(find "${SOURCES[@]}" -newer "$STAMP" -print -quit)" ]; then
      rebuild
    fi
  done
}

case "${1:-}" in
  serve) serve ;;
  wait) wait_ready ;;
  stop) stop ;;
  build) need_gems; build ;;
  check) python3 "$HERE/docs-check.py" "$HOME_DIR/work/_site" ;;
  *) echo "usage: docs-site.sh serve | wait | stop | build | check"; exit 2 ;;
esac
