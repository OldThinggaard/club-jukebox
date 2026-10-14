#!/bin/bash
# Turn the touchscreen light on. A full power-off leaves that light off,
# so the next start looks dead even though the fans are running.
# "wait" holds the jukebox screen until a display driver is present.
# Without one, Debian's own text stays on the screen.
set -u
sys="${JUKEBOX_SYS:-/sys}"
dri="${JUKEBOX_DRI:-/dev/dri}"
run="${JUKEBOX_RUN:-/run/jukebox}"
seconds="${JUKEBOX_PANEL_WAIT:-20}"

light_on() {
  local bl max
  for bl in "$sys"/class/backlight/*; do
    [[ -d "$bl" ]] || continue
    if [[ -f "$bl/bl_power" ]]; then
      printf '0\n' > "$bl/bl_power" 2>/dev/null || true
    fi
    if [[ -f "$bl/max_brightness" && -f "$bl/brightness" ]]; then
      max="$(tr -cd '0-9' < "$bl/max_brightness" 2>/dev/null || true)"
      if [[ -n "$max" && "$max" -gt 0 ]]; then
        printf '%s\n' "$max" > "$bl/brightness" 2>/dev/null || true
      fi
    fi
  done
  for bl in "$sys"/class/leds/*; do
    [[ -d "$bl" ]] || continue
    case "$(basename "$bl")" in
      *backlight*|*lcd*|*panel*) ;;
      *) continue ;;
    esac
    if [[ -f "$bl/max_brightness" && -f "$bl/brightness" ]]; then
      max="$(tr -cd '0-9' < "$bl/max_brightness" 2>/dev/null || true)"
      if [[ -n "$max" ]]; then
        printf '%s\n' "$max" > "$bl/brightness" 2>/dev/null || true
      fi
    fi
  done
  for bl in "$sys"/class/graphics/fb*; do
    [[ -f "$bl/blank" ]] || continue
    printf '0\n' > "$bl/blank" 2>/dev/null || true
  done
  if [[ -w "$sys/module/kernel/parameters/consoleblank" ]]; then
    printf '0\n' > "$sys/module/kernel/parameters/consoleblank" 2>/dev/null || true
  fi
  if [[ "$sys" == "/sys" && -c /dev/tty1 ]]; then
    printf '\033[9;0]\033[14;0]' > /dev/tty1 2>/dev/null || true
  fi
}

real_gpu() {
  local card driver
  for card in "$sys"/class/drm/card[0-9]; do
    [[ -e "$card/device/driver" ]] || continue
    driver="$(basename "$(readlink -f "$card/device/driver" 2>/dev/null || true)")"
    case "$driver" in
      ""|simpledrm|efi-framebuffer) continue ;;
    esac
    return 0
  done
  return 1
}

any_gpu() {
  local card
  for card in "$dri"/card*; do
    [[ -e "$card" ]] || continue
    return 0
  done
  return 1
}

mark_ready() {
  install -d "$run" 2>/dev/null || mkdir -p "$run" 2>/dev/null || true
  printf '1\n' > "$run/panel-ready" 2>/dev/null || true
}

prepare() {
  light_on
  if ! pgrep -x Xorg >/dev/null 2>&1; then
    rm -f /tmp/.X0-lock /tmp/.X11-unix/X0
  fi
}

wait_for_panel() {
  local _n
  install -d "$run" 2>/dev/null || mkdir -p "$run" 2>/dev/null || true
  rm -f "$run/panel-ready"
  for _n in $(seq 1 "$seconds"); do
    light_on
    if real_gpu; then
      mark_ready
      return 0
    fi
    sleep 1
  done
  light_on
  if any_gpu; then
    mark_ready
  fi
  return 0
}

self_test() {
  local tmp="$1" bright power blank ready
  mkdir -p \
    "$tmp/sys/class/backlight/intel_backlight" \
    "$tmp/sys/class/drm/card0/device" \
    "$tmp/sys/class/graphics/fb0" \
    "$tmp/sys/bus/pci/drivers/i915" \
    "$tmp/dri" \
    "$tmp/run"
  printf '100\n' > "$tmp/sys/class/backlight/intel_backlight/max_brightness"
  printf '0\n' > "$tmp/sys/class/backlight/intel_backlight/brightness"
  printf '4\n' > "$tmp/sys/class/backlight/intel_backlight/bl_power"
  printf '1\n' > "$tmp/sys/class/graphics/fb0/blank"
  ln -sfn "$tmp/sys/bus/pci/drivers/i915" "$tmp/sys/class/drm/card0/device/driver"
  : > "$tmp/dri/card0"
  JUKEBOX_SYS="$tmp/sys" JUKEBOX_DRI="$tmp/dri" JUKEBOX_RUN="$tmp/run" JUKEBOX_PANEL_WAIT=2 \
    "$0" wait
  bright="$(tr -cd '0-9' < "$tmp/sys/class/backlight/intel_backlight/brightness")"
  power="$(tr -cd '0-9' < "$tmp/sys/class/backlight/intel_backlight/bl_power")"
  blank="$(tr -cd '0-9' < "$tmp/sys/class/graphics/fb0/blank")"
  [[ "$bright" == "100" && "$power" == "0" && "$blank" == "0" ]] || return 1
  [[ -f "$tmp/run/panel-ready" ]] || return 1

  rm -rf "$tmp/sys/class/drm" "$tmp/dri/card0" "$tmp/run/panel-ready"
  mkdir -p "$tmp/sys/class/drm"
  JUKEBOX_SYS="$tmp/sys" JUKEBOX_DRI="$tmp/dri" JUKEBOX_RUN="$tmp/run" JUKEBOX_PANEL_WAIT=1 \
    "$0" wait
  [[ ! -f "$tmp/run/panel-ready" ]] || return 1
  return 0
}

case "${1:-}" in
  wait) wait_for_panel ;;
  prepare) prepare ;;
  self-test)
    tmp="$(mktemp -d)"
    if self_test "$tmp"; then
      rm -rf "$tmp"
      exit 0
    fi
    rm -rf "$tmp"
    echo "panel-on self-test failed" >&2
    exit 1
    ;;
  *) light_on ;;
esac
exit 0
