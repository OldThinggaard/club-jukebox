#!/usr/bin/env bash
# Build publish/pakke/videojukebox.deb. One file. Double-click it on Debian.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
"$ROOT/deploy/scripts/publish.sh"

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
sed '/jukebox-installer/,/^fi$/d' "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/bin/session.sh" > "$pkg/opt/jukebox/bin/session.sh"
chmod 0755 "$pkg/opt/jukebox/bin/session.sh"
if grep -q jukebox-installer "$pkg/opt/jukebox/bin/session.sh"; then
  echo "The package still opens the disk screen." >&2
  exit 1
fi
install -m 0755 \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-open.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-login.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime-boot.sh" \
  "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/wifi-connect.sh" \
  "$pkg/opt/jukebox/lib/"
install -m 0755 "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/lib/onlime_prompts.py" "$pkg/opt/jukebox/lib/onlime_prompts.py"
install -m 0644 "$ROOT/deploy/live/apt/jotta.key.chroot" "$pkg/usr/share/jukebox/jotta.gpg"
install -m 0644 "$ROOT/deploy/avahi/jukebox.service" "$pkg/etc/avahi/services/jukebox.service"
install -m 0755 "$ROOT/deploy/package/usb-package.py" "$pkg/opt/jukebox/lib/usb-package.py"
install -m 0755 "$ROOT/deploy/package/onlime-install.sh" "$pkg/opt/jukebox/lib/onlime-install.sh"
install -m 0644 "$ROOT/deploy/systemd/jukebox-update.service" "$pkg/etc/systemd/system/jukebox-update.service"
install -m 0644 "$ROOT/deploy/systemd/jukebox-usb-mount.service" "$pkg/etc/systemd/system/jukebox-usb-mount.service"
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
if ! grep -q "Tast koden" "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The package is missing the staff PIN screen." >&2
  exit 1
fi
if ! grep -q "Installer og genstart" "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The package is missing the USB install button." >&2
  exit 1
fi
if ! grep -q "Hent Onlime Sync" "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The package is missing the Onlime Sync button." >&2
  exit 1
fi
bash -n "$pkg/opt/jukebox/lib/onlime-install.sh"
python3 "$pkg/opt/jukebox/lib/usb-package.py" self-test
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
