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
    SINK="$(pactl list short sinks 2>/dev/null | awk '$2 ~ /analog/ { print $2; exit }')"
  fi
  if [[ -z "$PORT" ]]; then
    PORT="analog-output-speaker"
  fi
  if [[ "$SINK" != "$applied_sink" || "$PORT" != "$applied_port" ]]; then
    if command -v pactl >/dev/null 2>&1; then
      while read -r _ name _; do
        [[ -n "$name" ]] || continue
        pactl set-card-profile "$name" output:analog-stereo >/dev/null 2>&1 || true
      done < <(pactl list short cards 2>/dev/null || true)
      if [[ -n "$SINK" ]]; then
        pactl set-default-sink "$SINK" >/dev/null 2>&1 || true
        pactl set-sink-port "$SINK" "$PORT" >/dev/null 2>&1 || true
      fi
    fi
    applied_sink="$SINK"
    applied_port="$PORT"
  fi
  if command -v pactl >/dev/null 2>&1; then
    if [[ -n "$SINK" ]]; then
      pactl set-sink-mute "$SINK" 0 >/dev/null 2>&1 || true
      pactl set-sink-volume "$SINK" "${VOLUME}%" >/dev/null 2>&1 || true
    fi
    pactl set-sink-mute @DEFAULT_SINK@ 0 >/dev/null 2>&1 || true
  fi
  if command -v wpctl >/dev/null 2>&1; then
    wpctl set-mute @DEFAULT_AUDIO_SINK@ 0 >/dev/null 2>&1 || true
    wpctl set-volume @DEFAULT_AUDIO_SINK@ "${VOLUME}%" >/dev/null 2>&1 || true
  fi
  if command -v amixer >/dev/null 2>&1; then
    amixer -q sset Master "${VOLUME}%" unmute >/dev/null 2>&1 || true
    amixer -q sset Speaker unmute >/dev/null 2>&1 || true
    amixer -q sset Headphone unmute >/dev/null 2>&1 || true
    amixer -q sset PCM unmute >/dev/null 2>&1 || true
    amixer -q sset 'Auto-Mute Mode' Disabled >/dev/null 2>&1 || true
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
stop_play() {
  if [[ -n "${play_pid}" ]]; then
    kill "${play_pid}" >/dev/null 2>&1 || true
    wait "${play_pid}" >/dev/null 2>&1 || true
    play_pid=""
  fi
}

start_play() {
  local path="$1" kind="$2" duration="$3" art="$4"
  local screen seconds
  apply_audio
  screen="$(monitor_index || true)"
  local -a args
  args=(
    --no-terminal --really-quiet --no-border --ontop --keep-open=no
    --force-window=immediate --ao=pulse --volume="$VOLUME"
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
  elif [[ "$kind" == "audio" && -n "$art" && -f "$art" ]]; then
    args+=(--image-display-duration=inf --audio-file="$path" "$art")
  else
    args+=("$path")
  fi
  mpv "${args[@]}" >"$LOG" 2>&1 &
  play_pid=$!
  sleep 0.8
  if kill -0 "${play_pid}" >/dev/null 2>&1; then
    return 0
  fi
  wait "${play_pid}" >/dev/null 2>&1 || true
  # Pulse was not ready. Play through ALSA so the speakers still get the sound.
  local -a fallback
  fallback=(--no-terminal --really-quiet --no-border --ontop --keep-open=no --force-window=immediate --ao=alsa --volume="$VOLUME")
  if [[ -n "$screen" ]]; then
    fallback+=(--fs --fs-screen="$screen")
  else
    fallback+=(--fs)
  fi
  if [[ "$kind" == "image" ]]; then
    fallback+=(--image-display-duration="$seconds" "$path")
  elif [[ "$kind" == "audio" && -n "$art" && -f "$art" ]]; then
    fallback+=(--image-display-duration=inf --audio-file="$path" "$art")
  else
    fallback+=("$path")
  fi
  mpv "${fallback[@]}" >>"$LOG" 2>&1 &
  play_pid=$!
}

finish_if_stopped() {
  if [[ -z "${play_pid}" ]]; then
    return 0
  fi
  if kill -0 "${play_pid}" >/dev/null 2>&1; then
    return 0
  fi
  wait "${play_pid}" >/dev/null 2>&1 || true
  play_pid=""
  if [[ -n "${last}" ]]; then
    printf '%s\n' "${last}" > "$DONE"
  fi
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
