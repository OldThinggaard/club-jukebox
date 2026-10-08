#!/bin/bash
# Keep Onlime Sync running after the machine is installed. A machine that is not logged in does nothing.
set -u
if [[ ! -d "${HOME:-/home/jukebox}/.jottad" ]]; then
  exit 0
fi
export HOME="${HOME:-/home/jukebox}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export JOTTAD_SYSTEMD=0
if ! jotta-cli status >/dev/null 2>&1; then
  run_jottad || true
  sleep 2
fi
jotta-cli sync start || true
exit 0
