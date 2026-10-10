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

# The touch panel must not share its coordinates with the television.
# Otherwise a press lands beside the button it was aimed at.
internal_output() {
  xrandr --query 2>/dev/null | awk '$2=="connected" && $1 ~ /^(eDP|LVDS|DSI)/ { print $1; exit }'
}

layout_screens() {
  local panel hall
  panel="$(internal_output || true)"
  hall="$(xrandr --query 2>/dev/null | awk '$2=="connected" && $1 !~ /^(eDP|LVDS|DSI)/ { print $1; exit }')"
  if [[ -n "$panel" ]]; then
    xrandr --output "$panel" --auto --primary --pos 0x0 >/dev/null 2>&1 || true
  fi
  if [[ -n "$hall" && -n "$panel" ]]; then
    xrandr --output "$hall" --auto --right-of "$panel" >/dev/null 2>&1 || true
  fi
}

map_touch() {
  local panel id
  panel="$(internal_output || true)"
  [[ -n "$panel" ]] || return 0
  command -v xinput >/dev/null 2>&1 || return 0
  while read -r id; do
    [[ "$id" =~ ^[0-9]+$ ]] || continue
    if ! xinput list-props "$id" 2>/dev/null | grep -q "Abs .*Position X"; then
      continue
    fi
    xinput map-to-output "$id" "$panel" >/dev/null 2>&1 || true
  done < <(xinput list --id-only 2>/dev/null || true)
}

(
  while true; do
    panel="$(internal_output || true)"
    if [[ -n "$panel" ]] && ! xrandr --query 2>/dev/null | awk -v name="$panel" '$1==name && $2=="connected" { for (i=1;i<=NF;i++) if ($i ~ /^[0-9]+x[0-9]+\+0\+0$/) ok=1 } END { exit !ok }'; then
      layout_screens
    fi
    map_touch
    sleep 3
  done
) >/var/log/jukebox-touch.log 2>&1 &

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
