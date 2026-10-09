#!/bin/bash
# Open the Onlime security page in its own window so the long code can be copied.
set -euo pipefail
URL="https://cloud.onlime.dk/web/secure"
BROWSER="$(command -v chromium || command -v chromium-browser || true)"
if [[ -z "$BROWSER" ]]; then
  echo "Chromium findes ikke." >&2
  exit 1
fi
export DISPLAY="${DISPLAY:-:0}"
exec "$BROWSER" \
  --user-data-dir="${HOME:-/tmp}/.jukebox-onlime-browser" \
  --new-window \
  --window-size=1200,800 \
  --no-first-run \
  --disable-translate \
  "$URL"
