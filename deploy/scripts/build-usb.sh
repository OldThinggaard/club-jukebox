#!/usr/bin/env bash
# Build a hybrid ISO that installs Debian and the jukebox onto the PC disk.
# The ISO is written to publish/usb/jukebox-live-amd64.iso
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LIVE="$ROOT/deploy/live"
OUT="$ROOT/publish/usb"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DEBIAN_FRONTEND=noninteractive

if ! command -v lb >/dev/null 2>&1; then
  echo "live-build is not installed. Debian 13 has a current package. Ubuntu's live-build 3.x cannot build this UEFI image." >&2
  exit 1
fi
lb_version="$(lb --version 2>/dev/null || true)"
case "$lb_version" in
  3.*)
    echo "live-build ${lb_version} is too old for a UEFI USB image. Use live-build 2024 or newer (lb --version prints a date)." >&2
    exit 1
    ;;
esac

if [[ "${1:-}" != "--build-only" ]]; then
  "$ROOT/deploy/scripts/publish.sh"
fi

stage="$LIVE/config/includes.chroot/opt/jukebox"
rm -rf "$stage/current"
mkdir -p "$stage/current" "$stage/bin"
cp -a "$ROOT/publish/app/." "$stage/current/"
install -m 0755 "$ROOT/deploy/scripts/kiosk.sh" "$stage/bin/kiosk.sh"
mkdir -p "$LIVE/config/archives"
cp -a "$LIVE/apt/." "$LIVE/config/archives/"
chmod 0755 "$stage/bin/session.sh" "$LIVE/auto/config" \
  "$LIVE/config/hooks/normal/0100-jukebox.hook.chroot" \
  "$LIVE/config/hooks/live/0001-present.hook.chroot"

if [[ "${EUID}" -ne 0 ]]; then
  exec sudo --preserve-env=DEBIAN_FRONTEND "$0" --build-only "$@"
fi

cd "$LIVE"
if [[ " $* " == *" --clean "* ]]; then
  lb clean --purge || true
fi
lb config
lb build

iso="$(find "$LIVE" -maxdepth 1 -type f \( -name '*.hybrid.iso' -o -name '*.iso' \) | head -n 1)"
if [[ -z "$iso" ]]; then
  echo "live-build finished without an ISO" >&2
  exit 1
fi
mkdir -p "$OUT"
install -m 0644 "$iso" "$OUT/jukebox-live-amd64.iso"
sha256sum "$OUT/jukebox-live-amd64.iso" | tee "$OUT/jukebox-live-amd64.iso.sha256"
echo "ISO ready: $OUT/jukebox-live-amd64.iso"
