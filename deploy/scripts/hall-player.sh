#!/bin/bash
# Plays the current title on the television. Must run inside the graphical session.
set -u
export DISPLAY="${DISPLAY:-:0}"
export XAUTHORITY="${XAUTHORITY:-/home/jukebox/.Xauthority}"

monitor_index() {
  xrandr --listmonitors 2>/dev/null | awk '
    /^ *[0-9]+:/ {
      idx = $1
      gsub(":", "", idx)
      if ($0 ~ /eDP/ || $0 ~ /LVDS/ || $0 ~ /DSI/) next
      print idx
      exit
    }
  '
}

media_id() {
  printf '%s' "$1" | python3 -c 'import json,sys
raw = sys.stdin.read()
try:
    item = json.loads(raw).get("nowPlaying") or {}
except Exception:
    print("")
    raise SystemExit
print(item.get("mediaId") or "")
'
}

play_pid=""
stop_play() {
  if [[ -n "${play_pid}" ]]; then
    kill "${play_pid}" >/dev/null 2>&1 || true
    wait "${play_pid}" >/dev/null 2>&1 || true
    play_pid=""
  fi
}

last=""
while true; do
  json="$(curl -fsS --max-time 2 http://127.0.0.1:47123/api/screen 2>/dev/null || true)"
  id="$(media_id "$json")"
  if [[ "$id" == "$last" ]]; then
    if [[ -n "${play_pid}" ]] && ! kill -0 "${play_pid}" >/dev/null 2>&1; then
      play_pid=""
    fi
    sleep 1
    continue
  fi
  last="$id"
  stop_play
  if [[ -z "$id" ]]; then
    sleep 1
    continue
  fi
  screen="$(monitor_index || true)"
  args=(
    --no-terminal --really-quiet --no-border --ontop --keep-open=no
    --force-window=immediate --audio-fallback-to-null=yes
  )
  if [[ -n "$screen" ]]; then
    args+=(--fs --fs-screen="$screen")
  else
    args+=(--fs)
  fi
  mpv "${args[@]}" "http://127.0.0.1:47123/api/content/${id}" >/var/log/jukebox-mpv.log 2>&1 &
  play_pid=$!
  sleep 1
done
