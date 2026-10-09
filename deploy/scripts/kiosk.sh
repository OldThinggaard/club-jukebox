#!/usr/bin/env bash
# Places one Chromium window on the touchscreen or the television.
set -euo pipefail
ROLE="${1:-ui}"
ENV_FILE="${JUKEBOX_DISPLAYS:-/var/lib/jukebox/displays.env}"
incoming_hall="${HALL_GEOM:-}"
# shellcheck disable=SC1090
[[ -f "$ENV_FILE" ]] && source "$ENV_FILE"
if [[ -n "$incoming_hall" ]]; then
  HALL_GEOM="$incoming_hall"
fi

if [[ "$ROLE" == "hall" ]]; then
  GEOM="${HALL_GEOM:-}"
  URL="http://127.0.0.1:47123/player"
  PROFILE="${HOME}/.config/jukebox-hall"
  FULLSCREEN=0
else
  GEOM="${UI_GEOM:-}"
  URL="${JUKEBOX_KIOSK_URL:-http://127.0.0.1:47123/}"
  PROFILE="${HOME}/.config/jukebox-shelf"
  FULLSCREEN=1
fi

SIZE="1280,800"
POSITION="0,0"
if [[ "$GEOM" =~ ^([0-9]+)x([0-9]+)\+([0-9]+)\+([0-9]+)$ ]]; then
  SIZE="${BASH_REMATCH[1]},${BASH_REMATCH[2]}"
  POSITION="${BASH_REMATCH[3]},${BASH_REMATCH[4]}"
fi

BROWSER="$(command -v chromium || command -v chromium-browser || true)"
if [[ -z "$BROWSER" ]]; then
  echo "Chromium is not installed" >&2
  exit 1
fi

args=(
  --app="$URL"
  --user-data-dir="$PROFILE"
  --window-position="${POSITION%%,*},${POSITION##*,}"
  --window-size="${SIZE%%,*},${SIZE##*,}"
  --no-first-run
)
if [[ "$FULLSCREEN" == "1" ]]; then
  args+=(--kiosk --start-fullscreen)
fi
exec "$BROWSER" \
  "${args[@]}" \
  --disable-translate \
  --disable-features=TranslateUI \
  --autoplay-policy=no-user-gesture-required \
  --check-for-update-interval=31536000 \
  --renderer-process-limit=2 \
  --disable-dev-shm-usage \
  --overscroll-history-navigation=0
