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
started_epoch=0
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

wait_for_sink() {
  local n
  [[ -n "$SINK" ]] && return 0
  for n in 1 2 3 4 5 6 7 8; do
    sleep 0.4
    applied_sink=""
    apply_audio
    [[ -n "$SINK" ]] && return 0
  done
}

start_play() {
  local path="$1" kind="$2" duration="$3" art="$4"
  local screen seconds probed ao
  if [[ "$kind" != "image" ]]; then
    probed="$(probe_ms "$path")"
    if [[ "$probed" -ge 3000 ]]; then
      duration="$probed"
    elif [[ "$duration" -lt 20000 ]]; then
      duration=0
    fi
  fi
  apply_audio
  if [[ -z "$SINK" ]]; then
    wait_for_sink
  fi
  # One start. A second start makes the first song begin again.
  if [[ -n "$SINK" ]]; then
    ao="pulse"
  else
    ao="alsa"
  fi
  screen="$(monitor_index || true)"
  local -a args
  rm -f "$SOCK"
  args=(
    --no-config --no-terminal --really-quiet --no-border --ontop
    --keep-open=no --loop-file=no --idle=no --audio-display=no
    --no-osc --osd-level=0 --cursor-autohide=always
    --force-window=immediate --ao="$ao" --volume=100 --ao-volume=100
    --input-ipc-server="$SOCK"
  )
  if [[ "$ao" == "pulse" && -n "$SINK" ]]; then
    args+=(--audio-device="pulse/${SINK}")
  fi
  if [[ -n "$screen" ]]; then
    args+=(--fs --fs-screen="$screen")
  else
    args+=(--fs)
  fi
  if [[ "$kind" == "image" ]]; then
    seconds=$((duration / 1000))
    if [[ "$seconds" -lt 1 ]]; then
      seconds=8
    fi
    args+=(--image-display-duration="$seconds" "$path")
  else
    # The file itself must be what mpv is waiting on. A cover picture keeps
    # the player open after the song, so the next one in the queue never starts.
    args+=("$path")
  fi
  mpv "${args[@]}" >"$LOG" 2>&1 &
  play_pid=$!
  applied_volume=""
  sleep 0.4
  [[ -S "$SOCK" ]] && chmod 666 "$SOCK" 2>/dev/null || true
  play_ms="$duration"
  play_kind="$kind"
  started_epoch=$(date +%s)
}

past_limit() {
  [[ "$play_ms" -gt 0 && "$started_epoch" -gt 0 ]] || return 1
  local extra=8
  if [[ "$play_kind" == "image" ]]; then
    extra=2
  elif [[ "$play_ms" -lt 3000 ]]; then
    return 1
  fi
  local now limit
  now=$(date +%s)
  limit=$((play_ms / 1000 + extra))
  [[ $((now - started_epoch)) -ge $limit ]]
}

learn_length() {
  [[ -f /var/lib/jukebox/play.clock ]] || return 0
  [[ "$play_kind" == "image" ]] && return 0
  local cid ms
  cid="$(sed -n '1p' /var/lib/jukebox/play.clock 2>/dev/null || true)"
  ms="$(sed -n '3p' /var/lib/jukebox/play.clock 2>/dev/null || true)"
  [[ "$cid" == "$last" ]] || return 0
  case "$ms" in
    ''|*[!0-9]*) return 0 ;;
  esac
  if [[ "$ms" -ge 20000 ]]; then
    play_ms="$ms"
  fi
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
  local ended=0
  if [[ -z "${play_pid}" ]]; then
    return 0
  fi
  if [[ -S "$SOCK" ]]; then
    python3 /opt/jukebox/lib/mpv-volume.py "$SOCK" - "$last" >/dev/null 2>&1
    if [[ "$?" -eq 2 ]]; then
      ended=1
    fi
  fi
  learn_length
  if [[ "$ended" -eq 1 ]] || past_limit; then
    note_finished "${last}"
    kill_play
    started_epoch=0
    return 0
  fi
  if kill -0 "${play_pid}" >/dev/null 2>&1; then
    return 0
  fi
  wait "${play_pid}" >/dev/null 2>&1 || true
  play_pid=""
  started_epoch=0
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
