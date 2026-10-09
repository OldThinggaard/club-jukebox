#!/bin/bash
# Log in to Onlime and start Sync. The token file is read once and not printed.
set -euo pipefail
TOKEN_FILE="${1:?token file}"
DEVICE="${2:-videojukebox}"
SYNC_ROOT="${3:-/var/lib/jukebox/onlime-sync}"
START="${4:-}"

if [[ ! -x /usr/bin/jotta-cli ]]; then
  echo "Onlime Sync er ikke installeret. Hent det under Onlime." >&2
  exit 1
fi

uid="$(id -u jukebox)"
runtime="/run/user/${uid}"
install -d -o jukebox -g jukebox -m 700 "$runtime" /home/jukebox "$SYNC_ROOT"
install -d -o jukebox -g jukebox "$SYNC_ROOT/reklamer" "$SYNC_ROOT/media" "$SYNC_ROOT/events" "$SYNC_ROOT/updates"
loginctl enable-linger jukebox >/dev/null 2>&1 || true
systemctl start "user@${uid}.service" >/dev/null 2>&1 || true

as_jukebox() {
  sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR="$runtime" "$@"
}

if ! as_jukebox jotta-cli status >/dev/null 2>&1; then
  as_jukebox run_jottad >/dev/null 2>&1 || true
fi

daemon=0
for _ in $(seq 1 30); do
  text="$(as_jukebox jotta-cli status 2>&1 || true)"
  if [[ "$text" == *"Account"* || "$text" == *"not logged in"* || "$text" == *"Not logged in"* ]]; then
    daemon=1
    break
  fi
  sleep 1
done
if [[ "$daemon" -ne 1 ]]; then
  echo "Onlime Sync startede ikke. Prøv igen." >&2
  exit 1
fi

if [[ "$text" != *"Account"* ]]; then
  as_jukebox python3 /opt/jukebox/lib/onlime_prompts.py login "$TOKEN_FILE" "$DEVICE"
fi

as_jukebox python3 /opt/jukebox/lib/onlime_prompts.py sync "$SYNC_ROOT" || true
as_jukebox jotta-cli config set syncpaused false >/dev/null 2>&1 || true
as_jukebox jotta-cli sync start >/dev/null 2>&1 || as_jukebox jotta-cli sync start --force >/dev/null 2>&1 || true

status="$(as_jukebox jotta-cli status 2>&1 || true)"
if [[ "$status" == *"not logged in"* || "$status" == *"Not logged in"* || "$status" != *"Account"* ]]; then
  echo "Onlime afviste koden. Lav en ny kode, og prøv igen." >&2
  exit 1
fi
if [[ "$status" != *"Sync"* ]]; then
  echo "Sync blev ikke startet. Læg filmene i Onlime under Sync, i mappen reklamer, og prøv igen." >&2
  exit 1
fi

if [[ "$START" == "--start" ]]; then
  setsid sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR="$runtime" \
    jotta-cli download --merge "Archive/reklamer" "$SYNC_ROOT/reklamer" \
    >/var/log/jukebox-onlime-download.log 2>&1 < /dev/null &
  echo "Sync er i gang. Læg filmene i Onlime under Sync, i mappen reklamer."
else
  echo "Onlime er logget ind. Filmene hentes ned til $SYNC_ROOT når maskinen er startet."
fi
