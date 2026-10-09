#!/usr/bin/env bash
# Build publish/pakke/videojukebox.deb. One file. Double-click it on Debian.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ ! -x "$ROOT/publish/app/Jukebox.Host" || ! -x "$ROOT/publish/updater/Jukebox.UpdateAgent" ]]; then
  "$ROOT/deploy/scripts/publish.sh"
fi

stage="$(mktemp -d)"
pkg="$stage/videojukebox"
mkdir -p \
  "$pkg/DEBIAN" \
  "$pkg/opt/jukebox/bin" \
  "$pkg/opt/jukebox/lib" \
  "$pkg/opt/jukebox/updater" \
  "$pkg/etc/avahi/services" \
  "$pkg/etc/X11" \
  "$pkg/etc/systemd/system" \
  "$pkg/etc/systemd/logind.conf.d" \
  "$pkg/usr/share/jukebox"

cp -a "$ROOT/publish/app/." "$pkg/opt/jukebox/current/"
cp -a "$ROOT/publish/updater/." "$pkg/opt/jukebox/updater/"
install -m 0755 "$ROOT/deploy/scripts/kiosk.sh" "$pkg/opt/jukebox/bin/kiosk.sh"
install -m 0755 "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/bin/session.sh" "$pkg/opt/jukebox/bin/session.sh"
install -m 0755 \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-open.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-login.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-boot.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/wifi-connect.sh" \
  "$pkg/opt/jukebox/lib/"
install -m 0755 "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime_prompts.py" "$pkg/opt/jukebox/lib/onlime_prompts.py"
install -m 0644 "$ROOT/deploy/live/apt/jotta.key.chroot" "$pkg/usr/share/jukebox/jotta.gpg"
install -m 0644 "$ROOT/deploy/avahi/jukebox.service" "$pkg/etc/avahi/services/jukebox.service"
install -m 0644 "$ROOT/deploy/systemd/jukebox-update.service" "$pkg/etc/systemd/system/jukebox-update.service"
printf '%s\n' 'allowed_users=anybody' 'needs_root_rights=no' > "$pkg/etc/X11/Xwrapper.config"
cat > "$pkg/etc/systemd/logind.conf.d/jukebox.conf" <<'EOF'
[Login]
HandleLidSwitch=ignore
HandleLidSwitchExternalPower=ignore
HandleLidSwitchDocked=ignore
IdleAction=ignore
EOF
install -m 0755 "$ROOT/deploy/package/debian/postinst" "$pkg/DEBIAN/postinst"
install -m 0755 "$ROOT/deploy/package/debian/prerm" "$pkg/DEBIAN/prerm"
install -m 0644 "$ROOT/deploy/package/debian/control" "$pkg/DEBIAN/control"

test -f "$pkg/opt/jukebox/current/Jukebox.Host"
test ! -e "$pkg/opt/jukebox/lib/install-to-disk.sh"
bash -n "$pkg/DEBIAN/postinst"

out="$ROOT/publish/pakke"
mkdir -p "$out"
rm -f "$out/videojukebox.deb" "$out/videojukebox.deb.sha256"
if command -v fakeroot >/dev/null 2>&1; then
  fakeroot -- bash -c 'chown -R root:root "$1" && dpkg-deb -Zxz --build "$1" "$2"' bash "$pkg" "$out/videojukebox.deb"
else
  sudo chown -R root:root "$pkg"
  sudo dpkg-deb -Zxz --build "$pkg" "$out/videojukebox.deb"
  sudo chown "$(id -u):$(id -g)" "$out/videojukebox.deb"
fi
sha256sum "$out/videojukebox.deb" | tee "$out/videojukebox.deb.sha256"
rm -rf "$stage"
echo "Package ready: $out/videojukebox.deb"
