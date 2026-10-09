#!/bin/bash
# Log in to Onlime and start Sync. The token file is read once and not printed.
set -euo pipefail
TOKEN_FILE="${1:?token file}"
DEVICE="${2:-videojukebox}"
SYNC_ROOT="${3:-/var/lib/jukebox/onlime-sync}"
if [[ ! -x /usr/bin/jotta-cli ]]; then
  echo "Onlime-programmet er ikke installeret på denne nøgle." >&2
  exit 1
fi
install -d -o jukebox -g jukebox "$SYNC_ROOT/media" "$SYNC_ROOT/events" "$SYNC_ROOT/updates" /home/jukebox
install -d -o jukebox -g jukebox -m 700 /run/user/1000
chown jukebox:jukebox /run/user/1000
if ! sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR=/run/user/1000 jotta-cli status >/dev/null 2>&1; then
  sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR=/run/user/1000 JOTTAD_SYSTEMD=0 run_jottad
fi
for _ in $(seq 1 30); do
  if sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR=/run/user/1000 jotta-cli status >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR=/run/user/1000 \
  python3 /opt/jukebox/lib/onlime_prompts.py login "$TOKEN_FILE" "$DEVICE"
sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR=/run/user/1000 \
  python3 /opt/jukebox/lib/onlime_prompts.py sync "$SYNC_ROOT"
if [[ "${4:-}" == "--start" ]]; then
  sudo -u jukebox env HOME=/home/jukebox XDG_RUNTIME_DIR=/run/user/1000 jotta-cli sync start
  echo "Onlime Sync henter nu filer ned til $SYNC_ROOT"
else
  echo "Onlime er logget ind. Filmene hentes ned til $SYNC_ROOT når disken er installeret."
fi
