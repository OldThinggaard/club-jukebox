#!/bin/bash
# Install Debian's signed shim and GRUB, and register them with the firmware.
# The machine can then start with Secure Boot on. The jukebox itself is unchanged.
set -u

BUNDLE="${JUKEBOX_SECURE_BOOT_DIR:-/usr/share/jukebox/secure-boot}"
LOG=/var/log/jukebox-secure-boot.log

say() {
  printf '%s\n' "$*" >&2
  if [[ "${1:-}" != "self-test" ]]; then
    printf '%s\n' "$*" >>"$LOG" 2>/dev/null || true
  fi
}

if [[ "${1:-}" == "self-test" ]]; then
  dir="${2:-$BUNDLE}"
  [[ -s "$dir/shimx64.efi.signed" && -s "$dir/grubx64.efi.signed" ]]
  exit
fi

if [[ ! -d /sys/firmware/efi ]]; then
  say "Maskinen bruger ikke UEFI. Secure Boot springes over."
  exit 0
fi

place() {
  local name="$1" dest="$2"
  if [[ -s "$dest" ]]; then
    return 0
  fi
  if [[ ! -s "$BUNDLE/$name" ]]; then
    return 1
  fi
  install -d "$(dirname "$dest")"
  install -m 0644 "$BUNDLE/$name" "$dest"
}

place shimx64.efi.signed /usr/lib/shim/shimx64.efi.signed || {
  say "FEJL: Debians godkendte shim mangler."
  exit 1
}
place grubx64.efi.signed /usr/lib/grub/x86_64-efi-signed/grubx64.efi.signed || {
  say "FEJL: Debians godkendte GRUB mangler."
  exit 1
}
place mmx64.efi.signed /usr/lib/shim/mmx64.efi.signed || true
place fbx64.efi.signed /usr/lib/shim/fbx64.efi.signed || true

if ! mountpoint -q /boot/efi; then
  mkdir -p /boot/efi
  if ! mount /boot/efi >>"$LOG" 2>&1; then
    say "FEJL: EFI-partitionen kunne ikke monteres."
    exit 1
  fi
fi
if ! mountpoint -q /sys/firmware/efi/efivars; then
  mkdir -p /sys/firmware/efi/efivars
  mount -t efivarfs efivarfs /sys/firmware/efi/efivars >>"$LOG" 2>&1 || true
fi

grub_default=/etc/default/grub
[[ -f "$grub_default" ]] || printf '%s\n' '# Videojukebox' >"$grub_default"
set_grub() {
  local key="$1" value="$2"
  if grep -q "^${key}=" "$grub_default"; then
    sed -i "s|^${key}=.*|${key}=${value}|" "$grub_default"
  else
    printf '%s\n' "${key}=${value}" >>"$grub_default"
  fi
}
set_grub GRUB_TIMEOUT 1
set_grub GRUB_TIMEOUT_STYLE hidden
set_grub GRUB_DISABLE_OS_PROBER true

if [[ ! -x /usr/sbin/update-grub || ! -x /usr/sbin/grub-install ]]; then
  say "FEJL: GRUB er ikke installeret."
  exit 1
fi
if ! /usr/sbin/update-grub >>"$LOG" 2>&1; then
  say "FEJL: Startmenuen blev ikke skrevet. Den nuværende opstart er beholdt."
  exit 1
fi
# --uefi-secure-boot is required: without it, GRUB writes an unsigned
# program when Secure Boot is currently off, and the PC then cannot start.
if ! /usr/sbin/grub-install \
  --target=x86_64-efi \
  --efi-directory=/boot/efi \
  --bootloader-id=debian \
  --uefi-secure-boot \
  --force-extra-removable >>"$LOG" 2>&1; then
  say "FEJL: Den godkendte start blev ikke skrevet til disken."
  exit 1
fi
if ! find /boot/efi \( -iname 'shimx64.efi' -o -iname 'BOOTX64.EFI' \) | grep -q .; then
  say "FEJL: Den godkendte start ligger ikke på EFI-partitionen."
  exit 1
fi
say "Disken kan starte med Secure Boot slået til."
exit 0
