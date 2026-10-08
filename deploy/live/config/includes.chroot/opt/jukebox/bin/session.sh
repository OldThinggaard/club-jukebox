#!/bin/bash
# X session for the live USB. The shelf is the only window on the touchscreen.
# A second output, when present, gets the hall page. mpv covers that output while a film plays.
set -u
export DISPLAY="${DISPLAY:-:0}"
export XAUTHORITY="${XAUTHORITY:-/home/jukebox/.Xauthority}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

if [[ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ]] && command -v dbus-launch >/dev/null 2>&1; then
  eval "$(dbus-launch --sh-syntax)"
fi

if command -v pipewire >/dev/null 2>&1; then
  pipewire >/var/log/jukebox-pipewire.log 2>&1 &
  sleep 0.3
  if command -v wireplumber >/dev/null 2>&1; then
    wireplumber >/var/log/jukebox-wireplumber.log 2>&1 &
  fi
  if command -v pipewire-pulse >/dev/null 2>&1; then
    pipewire-pulse >/var/log/jukebox-pipewire-pulse.log 2>&1 &
  fi
fi

xset s off || true
xset -dpms || true
xset s noblank || true
if command -v unclutter >/dev/null 2>&1; then
  unclutter -idle 3 -root >/dev/null 2>&1 &
fi

openbox >/var/log/jukebox-openbox.log 2>&1 &
openbox_pid=$!

ready=0
for _ in $(seq 1 180); do
  if curl -fsS http://127.0.0.1:47123/api/health >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done

if [[ "$ready" -ne 1 ]]; then
  echo "Jukebox did not answer on port 47123" >&2
fi

(
  for _ in $(seq 1 40); do
    if [[ -f /var/lib/jukebox/displays.env ]]; then
      # shellcheck disable=SC1091
      source /var/lib/jukebox/displays.env
      if [[ -n "${HALL_OUTPUT:-}" && "${HALL_OUTPUT}" != "${UI_OUTPUT:-}" ]]; then
        /opt/jukebox/bin/kiosk.sh hall &
      fi
      exit 0
    fi
    sleep 1
  done
) &

if [[ -f /etc/jukebox-installer ]]; then
  export JUKEBOX_KIOSK_URL="http://127.0.0.1:47123/install"
fi
/opt/jukebox/bin/kiosk.sh ui
status=$?
kill "$openbox_pid" >/dev/null 2>&1 || true
exit "$status"
