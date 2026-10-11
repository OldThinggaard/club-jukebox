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

# Once, after PipeWire is up: Debian's own volume to full, and the idle jack off.
# Nothing later moves these knobs.
if command -v wpctl >/dev/null 2>&1; then
  for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    if wpctl get-volume @DEFAULT_AUDIO_SINK@ >/dev/null 2>&1; then
      sleep 1
      wpctl set-mute @DEFAULT_AUDIO_SINK@ 0 || true
      wpctl set-volume @DEFAULT_AUDIO_SINK@ 1.0 || true
      if [[ -f /opt/jukebox/lib/mpv-volume.py ]]; then
        python3 /opt/jukebox/lib/mpv-volume.py active 100 || true
      fi
      break
    fi
    sleep 0.3
  done
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

panel_geom() {
  local panel="$1"
  xrandr --query 2>/dev/null | awk -v name="$panel" '$1==name && $2=="connected" { for (i=1;i<=NF;i++) if ($i ~ /^[0-9]+x[0-9]+\+[0-9]+\+[0-9]+$/) { print $i; exit } }'
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

xinput_bin() {
  if [[ -x /opt/jukebox/lib/xinput ]]; then
    printf '%s\n' /opt/jukebox/lib/xinput
  elif command -v xinput >/dev/null 2>&1; then
    command -v xinput
  fi
}

screen_size() {
  xrandr --query 2>/dev/null | awk '/^Screen / { for (i=1;i<=NF;i++) if ($i=="current") { print $(i+1), $(i+3); exit } }'
}

set_matrix() {
  local bin="$1" id="$2" panel="$3" sw sh geom ow oh ox oy c0 c2 c4 c5
  read -r sw sh < <(screen_size || true)
  geom="$(panel_geom "$panel" || true)"
  [[ "$geom" =~ ^([0-9]+)x([0-9]+)\+([0-9]+)\+([0-9]+)$ ]] || return 0
  ow="${BASH_REMATCH[1]}"
  oh="${BASH_REMATCH[2]}"
  ox="${BASH_REMATCH[3]}"
  oy="${BASH_REMATCH[4]}"
  [[ -n "${sw:-}" && "${sw:-0}" -gt 0 && -n "${sh:-}" && "${sh:-0}" -gt 0 ]] || return 0
  read -r c0 c2 c4 c5 < <(awk -v sw="$sw" -v sh="$sh" -v ow="$ow" -v oh="$oh" -v ox="$ox" -v oy="$oy" 'BEGIN { printf "%.6f %.6f %.6f %.6f\n", ow/sw, ox/sw, oh/sh, oy/sh }')
  "$bin" set-prop "$id" "Coordinate Transformation Matrix" "$c0" 0 "$c2" 0 "$c4" "$c5" 0 0 1 >/dev/null 2>&1 || true
}

map_touch() {
  local panel bin name id
  panel="$(internal_output || true)"
  [[ -n "$panel" ]] || return 0
  bin="$(xinput_bin || true)"
  if [[ -z "$bin" ]]; then
    printf '%s\n' "xinput mangler" >> /var/log/jukebox-touch.log
    return 0
  fi
  while IFS= read -r name; do
    name="${name%"${name##*[![:space:]]}"}"
    [[ -n "$name" ]] || continue
    case "$name" in
      Virtual*|*"XTEST"*) continue ;;
    esac
    while IFS= read -r id; do
      [[ "$id" =~ ^[0-9]+$ ]] || continue
      "$bin" map-to-output "$id" "$panel" >/dev/null 2>&1 || true
      set_matrix "$bin" "$id" "$panel"
      printf '%s\n' "touch $id ($name) -> $panel" >> /var/log/jukebox-touch.log
    done < <("$bin" list --id-only "$name" 2>/dev/null || true)
  done < <("$bin" list --name-only 2>/dev/null || true)
}

prepare_openbox() {
  local src="${HOME}/.config/openbox" system=/etc/xdg/openbox/rc.xml
  mkdir -p "$src"
  [[ -f "$system" ]] || return 0
  awk '
    /<\/applications>/ && !done {
      print "  <application class=\"*\">"
      print "    <decor>no</decor>"
      print "    <fullscreen>no</fullscreen>"
      print "    <maximized>no</maximized>"
      print "  </application>"
      done=1
    }
    { print }
  ' "$system" > "$src/rc.xml"
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

prepare_openbox
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
  rm -f /var/lib/jukebox/booting /var/lib/jukebox/recover /var/lib/jukebox/updating
fi

layout_screens
map_touch
panel="$(internal_output || true)"
if [[ -n "$panel" ]]; then
  geom="$(panel_geom "$panel" || true)"
  if [[ -n "$geom" ]]; then
    export UI_GEOM="$geom"
  fi
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
