#!/usr/bin/env bash
# Build a USB image that updates an already installed Videojukebox.
# It does not partition or erase the club disk.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="${JUKEBOX_LIVE_ISO:-$ROOT/publish/usb/jukebox-live-amd64.iso}"
OUT="$ROOT/publish/usb/videojukebox-opdater.iso"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [[ ! -f "$SRC" ]]; then
  echo "The base live ISO is missing: $SRC" >&2
  exit 1
fi
if [[ "${JUKEBOX_SKIP_DEB:-}" != 1 ]]; then
  "$ROOT/deploy/scripts/build-deb.sh"
fi
DEB="$ROOT/publish/pakke/videojukebox.deb"
python3 "$ROOT/deploy/package/update-installed.py" self-test

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
stage="$work/overlay"
mkdir -p \
  "$stage/opt/jukebox/lib" \
  "$stage/etc/systemd/system/multi-user.target.wants" \
  "$work/boot"

install -m 0755 "$ROOT/deploy/package/update-installed.py" "$stage/opt/jukebox/lib/update-installed.py"
install -m 0644 "$DEB" "$stage/opt/jukebox/lib/videojukebox.deb"
mkdir -p "$stage/opt/jukebox/bin"
sed '/jukebox-installer/,/^fi$/d' "$ROOT/deploy/live/config/includes.chroot/opt/jukebox/bin/session.sh" > "$stage/opt/jukebox/bin/session.sh"
chmod 0755 "$stage/opt/jukebox/bin/session.sh"
if grep -q jukebox-installer "$stage/opt/jukebox/bin/session.sh"; then
  echo "The update USB still contains the disk wipe screen." >&2
  exit 1
fi
cat > "$stage/etc/systemd/system/jukebox-usb-update.service" <<'EOF'
[Unit]
Description=Update the installed Videojukebox from this USB stick
DefaultDependencies=no
After=local-fs.target systemd-udev-settle.service
Conflicts=jukebox-graphical.service jukebox.service

[Service]
Type=oneshot
ExecStartPre=-/usr/bin/plymouth quit
ExecStartPre=-/usr/bin/chvt 1
ExecStart=/opt/jukebox/lib/update-installed.py
StandardInput=tty
StandardOutput=tty
StandardError=tty
TTYPath=/dev/tty1
TimeoutStartSec=0
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF
ln -s ../jukebox-usb-update.service "$stage/etc/systemd/system/multi-user.target.wants/jukebox-usb-update.service"

python3 - "$SRC" "$work/boot" <<'PY'
import subprocess, sys, os
src, dest = sys.argv[1], sys.argv[2]
names = [
    "/boot/grub/grub.cfg",
    "/boot/grub/loopback.cfg",
    "/boot/grub/x86_64-efi/grub.cfg",
    "/isolinux/live.cfg",
]
extra = " systemd.mask=jukebox-graphical.service systemd.mask=jukebox.service systemd.mask=jukebox-onlime.service"
for name in names:
    target = dest + name
    os_makedirs = __import__("os").makedirs
    os_makedirs(__import__("os").path.dirname(target), exist_ok=True)
    subprocess.check_call(["xorriso", "-osirrox", "on", "-indev", src, "-extract", name, target], stdout=subprocess.DEVNULL)
    os.chmod(target, 0o644)
    text = open(target, encoding="utf-8", errors="replace").read()
    if "boot=live" not in text or "jukebox-graphical.service" in text:
        continue
    lines = []
    for line in text.splitlines():
        if "boot=live" in line and "systemd.mask=jukebox-graphical.service" not in line:
            line += extra
        lines.append(line)
    open(target, "w", encoding="utf-8").write("\n".join(lines) + "\n")
PY

mksquashfs "$stage" "$work/zz-update.squashfs" -comp gzip -noappend -quiet
tmpout="$(mktemp /tmp/videojukebox-opdater.XXXXXX.iso)"
rm -f "$tmpout"
xorriso -indev "$SRC" -outdev "$tmpout" \
  -map "$work/zz-update.squashfs" /live/zz-update.squashfs \
  -map "$work/boot/boot/grub/grub.cfg" /boot/grub/grub.cfg \
  -map "$work/boot/boot/grub/loopback.cfg" /boot/grub/loopback.cfg \
  -map "$work/boot/boot/grub/x86_64-efi/grub.cfg" /boot/grub/x86_64-efi/grub.cfg \
  -map "$work/boot/isolinux/live.cfg" /isolinux/live.cfg \
  -boot_image any replay
mkdir -p "$(dirname "$OUT")"
if [[ -w "$(dirname "$OUT")" ]]; then
  mv "$tmpout" "$OUT"
else
  sudo install -m 0644 "$tmpout" "$OUT"
  rm -f "$tmpout"
fi
sha256sum "$OUT" | sudo tee "$OUT.sha256" >/dev/null
sha256sum "$OUT"
echo "Update USB ready: $OUT"
