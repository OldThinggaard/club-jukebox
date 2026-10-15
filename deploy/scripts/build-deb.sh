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
install -m 0755 "$ROOT/deploy/scripts/hall-player.sh" "$pkg/opt/jukebox/bin/hall-player.sh"
install -m 0755 "$ROOT/deploy/scripts/mpv-volume.py" "$pkg/opt/jukebox/lib/mpv-volume.py"
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
install -m 0755 "$ROOT/deploy/package/apply-pending.sh" "$pkg/opt/jukebox/lib/apply-pending.sh"
install -m 0755 "$ROOT/deploy/package/boot-guard.sh" "$pkg/opt/jukebox/lib/boot-guard.sh"
install -m 0755 "$ROOT/deploy/package/panel-on.sh" "$pkg/opt/jukebox/lib/panel-on.sh"
install -m 0755 "$ROOT/deploy/package/xinput" "$pkg/opt/jukebox/lib/xinput"
install -m 0755 "$ROOT/deploy/package/onlime-install.sh" "$pkg/opt/jukebox/lib/onlime-install.sh"
install -m 0644 "$ROOT/deploy/systemd/jukebox-update.service" "$pkg/etc/systemd/system/jukebox-update.service"
install -m 0644 "$ROOT/deploy/systemd/jukebox-pending.service" "$pkg/etc/systemd/system/jukebox-pending.service"
install -m 0644 "$ROOT/deploy/systemd/jukebox-boot-guard.service" "$pkg/etc/systemd/system/jukebox-boot-guard.service"
install -m 0644 "$ROOT/deploy/systemd/jukebox-panel.service" "$pkg/etc/systemd/system/jukebox-panel.service"
install -d "$pkg/etc/X11/xorg.conf.d"
cat > "$pkg/etc/X11/xorg.conf.d/10-jukebox.conf" <<'EOF'
Section "ServerFlags"
  Option "BlankTime" "0"
  Option "StandbyTime" "0"
  Option "SuspendTime" "0"
  Option "OffTime" "0"
EndSection
EOF
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
if ! grep -q "Kopier sangene" "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The package is missing the USB song button." >&2
  exit 1
fi
if ! grep -q "Hent Onlime Sync" "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The package is missing the Onlime Sync button." >&2
  exit 1
fi
bash -n "$pkg/opt/jukebox/lib/onlime-install.sh"
bash -n "$pkg/opt/jukebox/lib/apply-pending.sh"
bash -n "$pkg/opt/jukebox/lib/boot-guard.sh"
bash -n "$pkg/opt/jukebox/lib/panel-on.sh"
"$pkg/opt/jukebox/lib/panel-on.sh" self-test
bash -n "$pkg/opt/jukebox/bin/hall-player.sh"
bash -n "$pkg/opt/jukebox/bin/session.sh"
bash -n "$pkg/DEBIAN/prerm"
bash -n "$pkg/DEBIAN/postinst"
if grep -q -- '--now' "$pkg/DEBIAN/prerm"; then
  echo "prerm must not stop services during an upgrade." >&2
  exit 1
fi
if grep -q 'kill-after' "$pkg/opt/jukebox/lib/apply-pending.sh"; then
  echo "apply-pending must not kill dpkg." >&2
  exit 1
fi
if grep -q 'Before=jukebox' "$pkg/etc/systemd/system/jukebox-pending.service"; then
  echo "The package install must not block the screen." >&2
  exit 1
fi
if grep -q 'audio-fallback-to-null' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "The player must not drop the sound." >&2
  exit 1
fi
if grep -q '/api/content/' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "The player must read the file, not a web stream." >&2
  exit 1
fi
if grep -q 'image-display-duration=inf' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "A cover image must not keep the player open after the song." >&2
  exit 1
fi
if ! grep -q 'audio-display=no' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "The player must leave when the song ends." >&2
  exit 1
fi
if ! grep -q 'def active_output' "$pkg/opt/jukebox/lib/mpv-volume.py"; then
  echo "Startup must know which output is playing." >&2
  exit 1
fi
if ! grep -q 'wpctl set-volume @DEFAULT_AUDIO_SINK@ 0.8' "$pkg/opt/jukebox/bin/session.sh"; then
  echo "Debian's own volume must start at 80 percent." >&2
  exit 1
fi
if ! grep -q 'mpv-volume.py active 80' "$pkg/opt/jukebox/bin/session.sh"; then
  echo "The idle jack must be turned off once at startup." >&2
  exit 1
fi
if grep -q 'mpv-volume.py active' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "A song must not move the volume." >&2
  exit 1
fi
if grep -q 'wpctl set-volume' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "A song must not move Debian's volume." >&2
  exit 1
fi
if grep -q 'set-sink-port' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "The player must not switch the jack." >&2
  exit 1
fi
if ! grep -q -- '--volume=100' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "The file itself must stay at full level." >&2
  exit 1
fi
if grep -q 'alsactl init' "$pkg/opt/jukebox/bin/session.sh"; then
  echo "Startup must not reset the sound card." >&2
  exit 1
fi
if grep -q 'mpv-volume.py pipewire\|mpv-volume.py alsa' "$pkg/opt/jukebox/bin/hall-player.sh"; then
  echo "The player must not move every output." >&2
  exit 1
fi
if grep -q systemctl "$pkg/DEBIAN/postinst" "$pkg/DEBIAN/prerm" "$pkg/opt/jukebox/lib/boot-guard.sh" "$pkg/opt/jukebox/lib/apply-pending.sh"; then
  echo "An update must not talk to systemd." >&2
  exit 1
fi
if grep -q 'reboot -f' "$pkg/opt/jukebox/lib/boot-guard.sh"; then
  echo "A dark screen must not restart the computer." >&2
  exit 1
fi
if grep -q 'default.target' "$pkg/opt/jukebox/lib/boot-guard.sh"; then
  echo "A dark screen must not change Debian startup." >&2
  exit 1
fi
if grep -q 'jukebox-boot-guard.service sysinit.target' "$pkg/DEBIAN/postinst"; then
  echo "Boot must not run a script before the system is up." >&2
  exit 1
fi
if grep -q 'Before=sysinit.target' "$pkg/etc/systemd/system/jukebox-boot-guard.service"; then
  echo "Boot must not run a script before the system is up." >&2
  exit 1
fi
if ! grep -q 'panel-ready' "$pkg/DEBIAN/postinst"; then
  echo "The screen must be ready before it is taken." >&2
  exit 1
fi
if ! grep -q 'max_brightness' "$pkg/opt/jukebox/lib/panel-on.sh"; then
  echo "The panel light must be turned on at boot." >&2
  exit 1
fi
if grep -q -- '--kiosk' "$pkg/opt/jukebox/bin/kiosk.sh"; then
  echo "The touch window must stay on one screen." >&2
  exit 1
fi
if ! grep -q 'map-to-output' "$pkg/opt/jukebox/bin/session.sh"; then
  echo "Touch must be mapped to the panel." >&2
  exit 1
fi
if grep -q 'Lydstyrke' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The volume buttons must stay off the sound page." >&2
  exit 1
fi
if grep -q 'Indbyggede højttalere' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The speaker and headphone buttons must stay off the sound page." >&2
  exit 1
fi
if ! grep -q 'Tegn' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The keyboard must offer signs such as !." >&2
  exit 1
fi
if ! grep -q 'volume-rail' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The touch screen must have the volume slider." >&2
  exit 1
fi
if ! grep -q 'nowPlaying?.queueId' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The shelf must move the playing label when the song changes." >&2
  exit 1
fi
python3 "$pkg/opt/jukebox/lib/wifi-connect.sh" self-test
if ! grep -q 'Vis klubnavn' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The package is missing the club-name switch." >&2
  exit 1
fi
if ! grep -q 'song-text' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The television title must leave the picture between the opening and the ending." >&2
  exit 1
fi
if ! grep -q '/api/hall/finished' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "The television page must say when a song has ended." >&2
  exit 1
fi
if ! grep -q 'contextmenu' "$pkg/opt/jukebox/current/wwwroot/assets/"index-*.js; then
  echo "A long press must not open the white menu." >&2
  exit 1
fi
test -x "$pkg/opt/jukebox/lib/xinput"
python3 "$pkg/opt/jukebox/lib/usb-package.py" self-test

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
