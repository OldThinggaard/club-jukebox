#!/usr/bin/env bash
# Build publish/pakke/videojukebox-pakke.zip for a machine that already runs Debian 13.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

"$ROOT/deploy/scripts/publish.sh"

stage="$(mktemp -d)"
dest="$stage/videojukebox-pakke"
mkdir -p "$dest/bin" "$dest/lib" "$dest/avahi" "$dest/systemd"
cp -a "$ROOT/publish/app/." "$dest/app/"
cp -a "$ROOT/publish/updater/." "$dest/updater/"
install -m 0755 "$ROOT/deploy/scripts/kiosk.sh" "$dest/bin/kiosk.sh"
install -m 0755 "$ROOT/deploy/scripts/hide-cursor.py" "$dest/lib/hide-cursor.py"
install -m 0755 "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/bin/session.sh" "$dest/bin/session.sh"
install -m 0755 \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-open.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-login.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-boot.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/wifi-connect.sh" \
  "$dest/lib/"
install -m 0755 "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime_prompts.py" "$dest/lib/onlime_prompts.py"
install -m 0755 "$ROOT/deploy/package/panel-on.sh" "$dest/lib/panel-on.sh"
install -m 0644 "$ROOT/deploy/live/apt/jotta.key.chroot" "$dest/lib/jotta.gpg"
install -m 0644 "$ROOT/deploy/avahi/jukebox.service" "$dest/avahi/jukebox.service"
install -m 0644 "$ROOT/deploy/systemd/jukebox-update.service" "$dest/systemd/jukebox-update.service"
install -m 0755 "$ROOT/deploy/package/install.sh" "$dest/install.sh"
install -m 0644 "$ROOT/deploy/package/LÆS-MIG.txt" "$dest/LÆS-MIG.txt"
chmod 0755 "$dest/app/Jukebox.Host" "$dest/updater/Jukebox.UpdateAgent"

bash -n "$dest/install.sh"
test -f "$dest/app/Jukebox.Host"
test ! -e "$dest/lib/install-to-disk.sh"

out="$ROOT/publish/pakke"
mkdir -p "$out"
rm -f "$out/videojukebox-pakke.zip" "$out/videojukebox-pakke.zip.sha256"
(
  cd "$stage"
  zip -r -q "$out/videojukebox-pakke.zip" videojukebox-pakke
)
sha256sum "$out/videojukebox-pakke.zip" | tee "$out/videojukebox-pakke.zip.sha256"
rm -rf "$stage"
echo "Package ready: $out/videojukebox-pakke.zip"
