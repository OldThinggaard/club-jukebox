#!/bin/bash
# Wipe one chosen disk and install the running live system onto it.
# Refuses the USB stick itself and any disk that is already mounted.
set -euo pipefail

TARGET=""
CONFIRM=""
SOURCE=""
SKIP_BOOT=0
REBOOT=0
STATE_DIR="/tmp/jukebox-install"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --target) TARGET="${2:-}"; shift 2 ;;
    --confirm) CONFIRM="${2:-}"; shift 2 ;;
    --source) SOURCE="${2:-}"; shift 2 ;;
    --state) STATE_DIR="${2:-}"; shift 2 ;;
    --skip-boot) SKIP_BOOT=1; shift ;;
    --reboot) REBOOT=1; shift ;;
    *) echo "Ukendt argument: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$STATE_DIR"
: > "$STATE_DIR/status"
say() {
  printf '%s\n' "$*"
  printf '%s\n' "$*" >> "$STATE_DIR/status"
}

fail() {
  say "FEJL: $*"
  exit 1
}

[[ -n "$TARGET" && -n "$CONFIRM" ]] || fail "Disken er ikke valgt."
[[ -b "$TARGET" ]] || fail "Disken findes ikke."
if [[ ! -f /etc/jukebox-installer && "${JUKEBOX_INSTALL_TEST:-}" != 1 ]]; then
  fail "Installationen kan kun startes fra USB-nøglen."
fi
name="$(basename "$TARGET")"
[[ "$CONFIRM" == "$name" ]] || fail "Bekræftelsen matcher ikke den valgte disk."
if ! [[ "$TARGET" =~ ^/dev/(sd[a-z]+|vd[a-z]+|nvme[0-9]+n[0-9]+|mmcblk[0-9]+|loop[0-9]+)$ ]]; then
  fail "Disken kan ikke bruges."
fi
if lsblk -nr -o MOUNTPOINT "$TARGET" | awk 'NF { found=1 } END { exit !found }'; then
  fail "Disken er i brug. Vælg computerens egen harddisk, ikke USB-nøglen."
fi
while read -r node; do
  [[ -n "$node" ]] || continue
  label="$(blkid -s LABEL -o value "$node" 2>/dev/null || true)"
  label="${label//[[:space:]]/}"
  if [[ "${label^^}" == "JUKEBOX" ]]; then
    fail "Det er USB-nøglen selv. Vælg computerens harddisk."
  fi
done < <(lsblk -nr -o PATH "$TARGET")

if [[ -z "$SOURCE" ]]; then
  SOURCE="$(find /run/live /lib/live /cdrom -name filesystem.squashfs -print -quit 2>/dev/null || true)"
fi
[[ -n "$SOURCE" && -e "$SOURCE" ]] || fail "Installationsfilerne på nøglen blev ikke fundet."

if [[ "$TARGET" == *nvme* || "$TARGET" == *mmcblk* || "$TARGET" == *loop* ]]; then
  PART_BOOT="${TARGET}p1"
  PART_ROOT="${TARGET}p2"
else
  PART_BOOT="${TARGET}1"
  PART_ROOT="${TARGET}2"
fi

MNT="/mnt/jukebox-target"
cleanup() {
  mountpoint -q "$MNT/dev" && umount -l "$MNT/dev" || true
  mountpoint -q "$MNT/proc" && umount -l "$MNT/proc" || true
  mountpoint -q "$MNT/sys" && umount -l "$MNT/sys" || true
  mountpoint -q "$MNT/run" && umount -l "$MNT/run" || true
  mountpoint -q "$MNT/boot/efi" && umount "$MNT/boot/efi" || true
  mountpoint -q "$MNT" && umount "$MNT" || true
}
on_err() {
  say "FEJL: Installationen stoppede uventet (linje ${1})."
  exit 1
}
trap cleanup EXIT
trap 'on_err $LINENO' ERR

say "Sletter ${TARGET} og opretter nye partitioner."
wipefs -a "$TARGET"
sgdisk --zap-all "$TARGET" >/dev/null 2>&1 || true
parted -s "$TARGET" mklabel gpt
parted -s "$TARGET" mkpart ESP fat32 1MiB 513MiB
parted -s "$TARGET" set 1 esp on
parted -s "$TARGET" mkpart root ext4 513MiB 100%
partprobe "$TARGET" >/dev/null 2>&1 || true
partx -u "$TARGET" >/dev/null 2>&1 || true
for _ in 1 2 3 4 5 6 7 8 9 10; do
  [[ -b "$PART_BOOT" && -b "$PART_ROOT" ]] && break
  sleep 0.4
  partx -u "$TARGET" >/dev/null 2>&1 || true
done
[[ -b "$PART_BOOT" && -b "$PART_ROOT" ]] || fail "Partitionerne blev ikke oprettet."

say "Formatterer disken."
mkfs.vfat -F 32 -n EFI "$PART_BOOT" >/dev/null
mkfs.ext4 -F -L jukebox "$PART_ROOT" >/dev/null
mkdir -p "$MNT"
mount "$PART_ROOT" "$MNT"
mkdir -p "$MNT/boot/efi"
if ! mount "$PART_BOOT" "$MNT/boot/efi"; then
  if [[ "${JUKEBOX_INSTALL_TEST:-}" == 1 ]] && ! grep -qw vfat /proc/filesystems; then
    say "EFI-partitionen er formateret. Denne testkerne kan ikke montere vfat."
  else
    fail "EFI-partitionen kunne ikke monteres."
  fi
fi

say "Kopierer Videojukebox over på disken."
if [[ -d "$SOURCE" ]]; then
  mkdir -p "$MNT"
  tar -C "$SOURCE" --one-file-system -cf - . | tar -C "$MNT" -xf -
else
  unsquashfs -f -d "$MNT" "$SOURCE"
fi

ROOT_UUID="$(blkid -s UUID -o value "$PART_ROOT")"
EFI_UUID="$(blkid -s UUID -o value "$PART_BOOT")"
cat > "$MNT/etc/fstab" <<EOF
UUID=${ROOT_UUID} / ext4 defaults,noatime 0 1
UUID=${EFI_UUID} /boot/efi vfat umask=0077 0 2
EOF

rm -f "$MNT/etc/jukebox-installer"
rm -f "$MNT/etc/systemd/system/jukebox.service.d/installer.conf"
mkdir -p "$MNT/etc/systemd/system/jukebox.service.d"
cat > "$MNT/etc/systemd/system/jukebox.service.d/installed.conf" <<'EOF'
[Service]
Environment=Jukebox__InstallerMode=false
Environment=Jukebox__SeedSampleMedia=false
EOF

if [[ -f "$STATE_DIR/ssid" ]]; then
  say "Gemmer det trådløse netværk."
  install -d -m 755 "$MNT/etc/NetworkManager/system-connections"
  python3 /opt/jukebox/lib/wifi-connect.sh "$STATE_DIR" || true
  if [[ -f /etc/NetworkManager/system-connections/jukebox-wifi.nmconnection ]]; then
    install -m 600 /etc/NetworkManager/system-connections/jukebox-wifi.nmconnection \
      "$MNT/etc/NetworkManager/system-connections/jukebox-wifi.nmconnection"
  fi
fi

if [[ -d /home/jukebox/.jottad ]]; then
  say "Gemmer Onlime-login, så filmene kan hentes efter genstart."
  install -d -o jukebox -g jukebox "$MNT/home/jukebox"
  rm -rf "$MNT/home/jukebox/.jottad"
  cp -a /home/jukebox/.jottad "$MNT/home/jukebox/.jottad"
  chown -R jukebox:jukebox "$MNT/home/jukebox/.jottad"
  mkdir -p "$MNT/var/lib/systemd/linger"
  touch "$MNT/var/lib/systemd/linger/jukebox"
fi

if [[ "$SKIP_BOOT" -eq 0 ]]; then
  say "Gør disken startbar."
  for dir in dev proc sys run; do
    mkdir -p "$MNT/$dir"
    mount --bind "/$dir" "$MNT/$dir"
  done
  chroot "$MNT" apt-get remove -y live-boot live-config live-config-systemd >/dev/null 2>&1 || true
  chroot "$MNT" update-initramfs -u -k all
  chroot "$MNT" grub-install --target=x86_64-efi --efi-directory=/boot/efi --bootloader-id=jukebox --removable --no-nvram
  KERNEL="$(basename "$(ls -1 "$MNT"/boot/vmlinuz-* | head -n 1)")"
  INITRD="$(basename "$(ls -1 "$MNT"/boot/initrd.img-* | head -n 1)")"
  cat > "$MNT/boot/grub/grub.cfg" <<EOF
set timeout=2
set default=0
menuentry "Videojukebox" {
  search --fs-uuid --set=root ${ROOT_UUID}
  linux /boot/${KERNEL} root=UUID=${ROOT_UUID} ro quiet
  initrd /boot/${INITRD}
}
EOF
  umount "$MNT/dev" "$MNT/proc" "$MNT/sys" "$MNT/run" || true
fi

say "Installationen er færdig."
sync
if [[ "$REBOOT" -eq 1 ]]; then
  say "Maskinen genstarter nu uden USB-nøglen."
  systemctl reboot || reboot
fi
