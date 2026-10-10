#!/bin/bash
# If the previous boot never reached the screen, turn Debian's own desktop
# back on and restart once. This script must not talk to the service manager.
# That call deadlocks boot at "Started cups.service".
set -u
mark=/var/lib/jukebox/booting
updating=/var/lib/jukebox/updating
recover=/var/lib/jukebox/recover
pending=/var/lib/jukebox/pending/videojukebox.deb
install -d /var/lib/jukebox/failed-updates /var/lib/jukebox/pending 2>/dev/null || true

restore_desktop() {
  local target="" gdm=""
  if [[ -e /usr/lib/systemd/system/graphical.target ]]; then
    target=/usr/lib/systemd/system/graphical.target
  elif [[ -e /lib/systemd/system/graphical.target ]]; then
    target=/lib/systemd/system/graphical.target
  fi
  if [[ -n "$target" ]]; then
    ln -sfn "$target" /etc/systemd/system/default.target
  fi
  for gdm in \
    /usr/lib/systemd/system/gdm.service \
    /lib/systemd/system/gdm.service \
    /usr/lib/systemd/system/gdm3.service \
    /lib/systemd/system/gdm3.service
  do
    if [[ -e "$gdm" ]]; then
      ln -sfn "$gdm" /etc/systemd/system/display-manager.service
      install -d /etc/systemd/system/graphical.target.wants
      ln -sfn "$gdm" "/etc/systemd/system/graphical.target.wants/$(basename "$gdm")"
      break
    fi
  done
  rm -f \
    /etc/systemd/system/multi-user.target.wants/jukebox-graphical.service \
    /etc/systemd/system/graphical.target.wants/jukebox-graphical.service
}

if [[ -f "$mark" || -f "$updating" ]]; then
  if [[ -f "$recover" ]]; then
    rm -f "$mark" "$updating"
    exit 0
  fi
  if [[ -f "$pending" ]]; then
    mv "$pending" "/var/lib/jukebox/failed-updates/videojukebox-$(date +%s).deb" 2>/dev/null || true
  fi
  restore_desktop
  rm -f "$mark" "$updating"
  printf '%s\n' "1" > "$recover"
  sync
  printf '%s\n' "Videojukebox kom ikke op. Debian-skrivebordet slaaes til." > /dev/console || true
  /sbin/reboot -f || reboot -f || true
  exit 0
fi

touch "$mark" 2>/dev/null || true
if id jukebox >/dev/null 2>&1; then
  chown jukebox:jukebox "$mark" 2>/dev/null || true
fi
chmod 664 "$mark" 2>/dev/null || true
exit 0
