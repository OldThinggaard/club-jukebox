#!/bin/bash
# Put Debian's Microsoft-signed shim where the firmware actually boots.
# GRUB's own installer can write an unsigned program when Secure Boot is off.
# The firmware rejects that program, so the shim is copied here directly.
set -u

BUNDLE="${JUKEBOX_SECURE_BOOT_DIR:-/usr/share/jukebox/secure-boot}"
LOG=/var/log/jukebox-secure-boot.log

say() {
  printf '%s\n' "$*" >&2
  printf '%s\n' "$*" >>"$LOG" 2>/dev/null || true
}

install_loaders() {
  local esp="$1" uuid="$2" dir name
  [[ -s "$BUNDLE/shimx64.efi.signed" && -s "$BUNDLE/grubx64.efi.signed" ]] || return 1
  for name in BOOT debian jukebox; do
    dir="$esp/EFI/$name"
    install -d "$dir"
    install -m 0644 "$BUNDLE/grubx64.efi.signed" "$dir/grubx64.efi"
    if [[ -s "$BUNDLE/mmx64.efi.signed" ]]; then
      install -m 0644 "$BUNDLE/mmx64.efi.signed" "$dir/mmx64.efi"
    fi
    if [[ -s "$BUNDLE/fbx64.efi.signed" ]]; then
      install -m 0644 "$BUNDLE/fbx64.efi.signed" "$dir/fbx64.efi"
    fi
    if [[ "$name" == "BOOT" ]]; then
      install -m 0644 "$BUNDLE/shimx64.efi.signed" "$dir/BOOTX64.EFI"
    else
      install -m 0644 "$BUNDLE/shimx64.efi.signed" "$dir/shimx64.efi"
    fi
    cat >"$dir/grub.cfg" <<EOF
search.fs_uuid ${uuid} root
set prefix=(\$root)'/boot/grub'
configfile \$prefix/grub.cfg
EOF
  done
  cmp -s "$BUNDLE/shimx64.efi.signed" "$esp/EFI/BOOT/BOOTX64.EFI"
}

if [[ "${1:-}" == "self-test" ]]; then
  BUNDLE="${2:-$BUNDLE}"
  tmp="$(mktemp -d)"
  install_loaders "$tmp" TEST-UUID
  status=$?
  rm -rf "$tmp"
  exit "$status"
fi

if [[ ! -d /sys/firmware/efi ]]; then
  say "Maskinen bruger ikke UEFI. Secure Boot springes over."
  exit 0
fi
if [[ ! -s "$BUNDLE/shimx64.efi.signed" || ! -s "$BUNDLE/grubx64.efi.signed" ]]; then
  say "FEJL: Debians godkendte shim mangler."
  exit 1
fi

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

root_uuid="$(findmnt -n -o UUID / 2>/dev/null || true)"
if [[ -z "$root_uuid" ]]; then
  root_uuid="$(awk '$2=="/"{print $1; exit}' /etc/fstab 2>/dev/null || true)"
  root_uuid="${root_uuid#UUID=}"
fi
if [[ -z "$root_uuid" ]]; then
  say "FEJL: Diskens id blev ikke fundet."
  exit 1
fi

if [[ -x /usr/sbin/update-grub ]]; then
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
  /usr/sbin/update-grub >>"$LOG" 2>&1 || say "Startmenuen blev ikke fornyet. Den gamle beholdes."
fi

if ! install_loaders /boot/efi "$root_uuid"; then
  say "FEJL: Den godkendte start blev ikke skrevet til disken."
  exit 1
fi

if command -v efibootmgr >/dev/null 2>&1; then
  esp_src="$(findmnt -n -o SOURCE /boot/efi 2>/dev/null || true)"
  esp_src="$(readlink -f "$esp_src" 2>/dev/null || true)"
  pkname="$(lsblk -no PKNAME "$esp_src" 2>/dev/null | awk 'NF{print; exit}')"
  part="$(lsblk -no PARTN "$esp_src" 2>/dev/null | awk 'NF{print; exit}')"
  while read -r line; do
    id="$(printf '%s\n' "$line" | sed -n 's/^Boot\([0-9A-Fa-f]\{4\}\).*/\1/p')"
    [[ -n "$id" ]] || continue
    case "${line,,}" in
      *grubx64.efi*) efibootmgr -b "$id" -B >>"$LOG" 2>&1 || true ;;
    esac
  done < <(efibootmgr -v 2>/dev/null || true)
  if [[ -n "$pkname" && -n "$part" ]]; then
    created="$(efibootmgr -c -d "/dev/$pkname" -p "$part" -L debian -l '\EFI\debian\shimx64.efi' 2>&1 | tee -a "$LOG" || true)"
    new_id="$(printf '%s\n' "$created" | sed -n 's/^Boot\([0-9A-Fa-f]\{4\}\).*/\1/p' | head -n 1)"
    if [[ -n "$new_id" ]]; then
      order="$(efibootmgr | sed -n 's/^BootOrder: //p' | tr -d ' ')"
      next="$new_id"
      IFS=',' read -ra parts <<<"$order"
      for item in "${parts[@]}"; do
        [[ -n "$item" && "$item" != "$new_id" ]] || continue
        next+=",$item"
      done
      efibootmgr -o "$next" >>"$LOG" 2>&1 || true
    fi
  fi
  if efibootmgr -v 2>/dev/null | grep -qi 'grubx64.efi'; then
    say "FEJL: PC'en har stadig en opstartspost, der peger på det gamle program."
    exit 1
  fi
fi

sync
say "Disken kan starte med Secure Boot slået til."
exit 0
