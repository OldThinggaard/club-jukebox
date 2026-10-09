#!/bin/bash
# Keep Onlime Sync running after a reboot. A machine that is not logged in does nothing.
set -u
home="${HOME:-/home/jukebox}"
if [[ ! -d "$home/.jottad" ]]; then
  exit 0
fi
export HOME="$home"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
if ! jotta-cli status >/dev/null 2>&1; then
  run_jottad >/dev/null 2>&1 || true
  sleep 2
fi
jotta-cli config set syncpaused false >/dev/null 2>&1 || true
jotta-cli sync start >/dev/null 2>&1 || true
sync_root="${JUKEBOX_DATA_DIR:-/var/lib/jukebox}/onlime-sync"
if [[ -d "$sync_root/reklamer" ]]; then
  setsid jotta-cli download --merge "Archive/reklamer" "$sync_root/reklamer" >/var/log/jukebox-onlime-download.log 2>&1 < /dev/null &
fi
exit 0
