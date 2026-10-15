#!/bin/bash
# Plays the current title on the television, from the file itself.
# The sound is routed here, inside the session that owns PipeWire.
set -u
export DISPLAY="${DISPLAY:-:0}"
export XAUTHORITY="${XAUTHORITY:-/home/jukebox/.Xauthority}"
runtime="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export XDG_RUNTIME_DIR="$runtime"
export PULSE_SERVER="unix:${runtime}/pulse/native"

REQUEST=/var/lib/jukebox/play.request
DONE=/var/lib/jukebox/play.done
STAMP=/var/lib/jukebox/player.stamp
AUDIO=/var/lib/jukebox/audio.env
LOG=/var/log/jukebox-mpv.log

SINK=""
PORT=""
VOLUME=80
applied_sink=""
applied_port=""
applied_volume=""
SOCK=/tmp/jukebox-mpv.sock

for _ in $(seq 1 30); do
  if [[ -S "${runtime}/pulse/native" ]]; then
    break
  fi
  sleep 0.5
done

read_audio() {
  SINK=""
  PORT=""
  VOLUME=80
  [[ -f "$AUDIO" ]] || return 0
  while IFS='=' read -r key value; do
    value="${value//$'\r'/}"
    case "$key" in
      SINK) SINK="$value" ;;
      PORT) PORT="$value" ;;
      VOLUME) VOLUME="$value" ;;
    esac
  done < "$AUDIO"
  case "$VOLUME" in
    ''|*[!0-9]*) VOLUME=80 ;;
  esac
  if [[ "$VOLUME" -gt 100 ]]; then
    VOLUME=100
  fi
}

apply_audio() {
  read_audio
  # Keep the sink we already found. Asking pactl on every pass stalled the
  # loop, so a finished song was never handed to the next one.
  if [[ -z "$SINK" && -n "$applied_sink" ]]; then
    SINK="$applied_sink"
  fi
  if [[ -z "$SINK" ]] && command -v pactl >/dev/null 2>&1; then
    SINK="$(timeout 2 pactl list short sinks 2>/dev/null | awk '$2 ~ /analog/ { print $2; exit }')"
  fi
  if [[ -z "$PORT" ]]; then
    PORT="analog-output-speaker"
  fi
  if [[ "$SINK" != "$applied_sink" || "$PORT" != "$applied_port" ]]; then
    # The headset jack switches the output by itself. Leave that alone.
    applied_sink="$SINK"
    applied_port="$PORT"
  fi
}

probe_ms() {
  local file="$1" raw
  raw="$(timeout 8 ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "$file" 2>/dev/null || true)"
  raw="${raw%%.*}"
  case "$raw" in
    ''|*[!0-9]*) printf '%s\n' 0; return 0 ;;
  esac
  printf '%s\n' "$((raw * 1000))"
}

monitor_index() {
  timeout 2 xrandr --listmonitors 2>/dev/null | awk '
    /^ *[0-9]+:/ {
      idx = $1
      gsub(":", "", idx)
      if ($0 ~ /eDP/ || $0 ~ /LVDS/ || $0 ~ /DSI/) next
      print idx
      exit
    }
  '
}

play_pid=""
play_ms=0
play_kind=""
kill_play() {
  [[ -n "${play_pid}" ]] || return 0
  local pid="${play_pid}" n
  kill "$pid" >/dev/null 2>&1 || true
  for n in 1 2 3 4 5 6 7 8 9 10; do
    if ! kill -0 "$pid" >/dev/null 2>&1; then
      wait "$pid" >/dev/null 2>&1 || true
      play_pid=""
      return 0
    fi
    if [[ "$n" -eq 4 ]]; then
      kill -9 "$pid" >/dev/null 2>&1 || true
    fi
    sleep 0.2
  done
  kill -9 "$pid" >/dev/null 2>&1 || true
  wait "$pid" >/dev/null 2>&1 || true
  play_pid=""
}

stop_play() {
  kill_play
}

launch_mpv() {
  local path="$1" kind="$2"
  local screen seconds
  apply_audio
  screen="$(monitor_index || true)"
  local -a args
  rm -f "$SOCK"
  args=(
    --no-config --no-terminal --really-quiet --no-border --ontop
    --keep-open=no --loop-file=no --idle=no --audio-display=no
    --no-osc --osd-level=0 --cursor-autohide=always
    --force-window=immediate --ao=pulse --volume=100 --ao-volume=100
    --input-ipc-server="$SOCK"
  )
  if [[ -n "$SINK" ]]; then
    args+=(--audio-device="pulse/${SINK}")
  fi
  if [[ -n "$screen" ]]; then
    args+=(--fs --fs-screen="$screen")
  else
    args+=(--fs)
  fi
  if [[ "$kind" == "image" ]]; then
    seconds=$((play_ms / 1000))
    if [[ "$seconds" -lt 1 ]]; then
      seconds=8
    fi
    args+=(--image-display-duration="$seconds" "$path")
  else
    # The file itself must be what mpv is waiting on. A cover picture keeps
    # the player open after the song, so the next one in the queue never starts.
    args+=("$path")
  fi
  # One open. A second open starts the same song from the beginning.
  mpv "${args[@]}" >"$LOG" 2>&1 &
  play_pid=$!
  applied_volume=""
  sleep 0.4
  [[ -S "$SOCK" ]] && chmod 666 "$SOCK" 2>/dev/null || true
}

start_play() {
  local path="$1" kind="$2" duration="$3" art="$4"
  play_kind="$kind"
  play_ms="$duration"
  launch_mpv "$path" "$kind"
}

note_finished() {
  local id="$1"
  [[ -n "$id" ]] || return 0
  printf '%s\n' "$id" > "$DONE"
  curl -fsS --max-time 2 -X POST http://127.0.0.1:47123/api/hall/finished \
    -H 'Content-Type: application/json' \
    -d "{\"queueId\":\"${id}\"}" >/dev/null 2>&1 || true
}

finish_if_stopped() {
  if [[ -z "${play_pid}" ]]; then
    return 0
  fi
  if [[ -S "$SOCK" ]]; then
    python3 /opt/jukebox/lib/mpv-volume.py "$SOCK" - "$last" >/dev/null 2>&1 || true
  fi
  if kill -0 "${play_pid}" >/dev/null 2>&1; then
    return 0
  fi
  # The player itself stopped. Do not open the file again.
  wait "${play_pid}" >/dev/null 2>&1 || true
  play_pid=""
  note_finished "${last}"
}

last=""
while true; do
  touch "$STAMP" 2>/dev/null || true
  apply_audio
  if [[ ! -f "$REQUEST" ]]; then
    finish_if_stopped
    sleep 0.4
    continue
  fi
  id="$(sed -n '1p' "$REQUEST")"
  path="$(sed -n '2p' "$REQUEST")"
  kind="$(sed -n '3p' "$REQUEST")"
  duration="$(sed -n '4p' "$REQUEST")"
  art="$(sed -n '5p' "$REQUEST")"
  case "$duration" in
    ''|*[!0-9]*) duration=0 ;;
  esac
  if [[ "$id" == "$last" ]]; then
    finish_if_stopped
    sleep 0.4
    continue
  fi
  last="$id"
  stop_play
  if [[ -z "$id" || -z "$path" || ! -f "$path" ]]; then
    printf '%s\n' "$id" > "$DONE"
    sleep 0.4
    continue
  fi
  start_play "$path" "$kind" "$duration" "$art"
  sleep 0.4
done
