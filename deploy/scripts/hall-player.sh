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
SOCK="${runtime}/jukebox-mpv.sock"

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
  if [[ -z "$SINK" ]] && command -v pactl >/dev/null 2>&1; then
    SINK="$(timeout 2 pactl list short sinks 2>/dev/null | awk '$2 ~ /analog/ { print $2; exit }')"
  fi
  if [[ -z "$PORT" ]]; then
    PORT="analog-output-speaker"
  fi
  if [[ "$SINK" != "$applied_sink" || "$PORT" != "$applied_port" ]]; then
    if command -v pactl >/dev/null 2>&1; then
      while read -r _ name _; do
        [[ -n "$name" ]] || continue
        timeout 2 pactl set-card-profile "$name" output:analog-stereo >/dev/null 2>&1 || true
      done < <(timeout 2 pactl list short cards 2>/dev/null || true)
      timeout 2 pactl set-sink-mute @DEFAULT_SINK@ 0 >/dev/null 2>&1 || true
      timeout 2 pactl set-sink-volume @DEFAULT_SINK@ 100% >/dev/null 2>&1 || true
      if [[ -n "$SINK" ]]; then
        timeout 2 pactl set-default-sink "$SINK" >/dev/null 2>&1 || true
        timeout 2 pactl set-sink-port "$SINK" "$PORT" >/dev/null 2>&1 || true
        timeout 2 pactl set-sink-mute "$SINK" 0 >/dev/null 2>&1 || true
        timeout 2 pactl set-sink-volume "$SINK" 100% >/dev/null 2>&1 || true
      fi
    fi
    if command -v wpctl >/dev/null 2>&1; then
      timeout 2 wpctl set-mute @DEFAULT_AUDIO_SINK@ 0 >/dev/null 2>&1 || true
      timeout 2 wpctl set-volume @DEFAULT_AUDIO_SINK@ 100% >/dev/null 2>&1 || true
    fi
    if command -v amixer >/dev/null 2>&1; then
      timeout 2 amixer -q sset Master 100% unmute >/dev/null 2>&1 || true
      timeout 2 amixer -q sset Speaker 100% unmute >/dev/null 2>&1 || true
      timeout 2 amixer -q sset Headphone 100% unmute >/dev/null 2>&1 || true
      timeout 2 amixer -q sset PCM 100% unmute >/dev/null 2>&1 || true
      timeout 2 amixer -q sset 'Auto-Mute Mode' Disabled >/dev/null 2>&1 || true
    fi
    applied_sink="$SINK"
    applied_port="$PORT"
  fi
  push_volume
}

push_volume() {
  if [[ "$VOLUME" == "$applied_volume" || ! -S "$SOCK" ]]; then
    return 0
  fi
  if python3 /opt/jukebox/lib/mpv-volume.py "$SOCK" "$VOLUME"; then
    applied_volume="$VOLUME"
  fi
}

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

start_play() {
  local path="$1" kind="$2" duration="$3" art="$4"
  local screen seconds
  apply_audio
  screen="$(monitor_index || true)"
  local -a args
  rm -f "$SOCK"
  args=(
    --no-config --no-terminal --really-quiet --no-border --ontop
    --keep-open=no --loop-file=no --idle=no --audio-display=no
    --no-osc --osd-level=0 --cursor-autohide=always
    --force-window=immediate --ao=pulse --volume="$VOLUME"
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
  sleep 0.8
  push_volume
  if kill -0 "${play_pid}" >/dev/null 2>&1; then
    play_ms="$duration"
    play_kind="$kind"
    started_epoch=$(date +%s)
    return 0
  fi
  wait "${play_pid}" >/dev/null 2>&1 || true
  # Pulse was not ready. Play through ALSA so the speakers still get the sound.
  local -a fallback
  rm -f "$SOCK"
  fallback=(--no-config --no-terminal --really-quiet --no-border --ontop --keep-open=no --loop-file=no --idle=no --audio-display=no --no-osc --osd-level=0 --cursor-autohide=always --force-window=immediate --ao=alsa --volume="$VOLUME" --input-ipc-server="$SOCK")
  if [[ -n "$screen" ]]; then
    fallback+=(--fs --fs-screen="$screen")
  else
    fallback+=(--fs)
  fi
  if [[ "$kind" == "image" ]]; then
    fallback+=(--image-display-duration="$seconds" "$path")
  else
    fallback+=("$path")
  fi
  mpv "${fallback[@]}" >>"$LOG" 2>&1 &
  play_pid=$!
  applied_volume=""
  sleep 0.4
  push_volume
  play_ms="$duration"
  play_kind="$kind"
  started_epoch=$(date +%s)
}

past_limit() {
  [[ "$play_ms" -gt 0 && "$started_epoch" -gt 0 ]] || return 1
  local extra=8
  if [[ "$play_kind" == "image" ]]; then
    extra=2
  elif [[ "$play_ms" -lt 20000 ]]; then
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
