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
else
  # boot-guard.sh leaves this file. Removing it means this boot reached the screen.
  rm -f /var/lib/jukebox/booting
fi

hall_geom() {
  xrandr --query 2>/dev/null | awk '
    $2 == "connected" && $1 !~ /^(eDP|LVDS|DSI)/ {
      for (i = 1; i <= NF; i++) if ($i ~ /^[0-9]+x[0-9]+\+[0-9]+\+[0-9]+$/) { print $i; exit }
    }
  '
}

(
  for _ in $(seq 1 90); do
    geom="$(hall_geom || true)"
    if [[ -n "$geom" ]]; then
      HALL_GEOM="$geom" /opt/jukebox/bin/kiosk.sh hall &
      exit 0
    fi
    sleep 2
  done
) &

/opt/jukebox/bin/hall-player.sh >/var/log/jukebox-hall-player.log 2>&1 &

if [[ -f /etc/jukebox-installer ]]; then
  export JUKEBOX_KIOSK_URL="http://127.0.0.1:47123/install"
fi
/opt/jukebox/bin/kiosk.sh ui
status=$?
kill "$openbox_pid" >/dev/null 2>&1 || true
exit "$status"
