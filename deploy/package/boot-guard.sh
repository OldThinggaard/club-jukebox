#!/bin/bash
# If the previous boot never reached the screen, turn Debian's own desktop
# back on. This script must not run dpkg. Killing dpkg is what breaks Debian.
set -u
mark=/var/lib/jukebox/booting
updating=/var/lib/jukebox/updating
pending=/var/lib/jukebox/pending/videojukebox.deb
install -d /var/lib/jukebox/failed-updates /var/lib/jukebox/pending 2>/dev/null || true

if [[ -f "$mark" || -f "$updating" ]]; then
  if [[ -f "$pending" ]]; then
    mv "$pending" "/var/lib/jukebox/failed-updates/videojukebox-$(date +%s).deb" 2>/dev/null || true
  fi
  timeout 8 systemctl enable gdm.service >/dev/null 2>&1 || true
  timeout 8 systemctl enable gdm3.service >/dev/null 2>&1 || true
  timeout 8 systemctl set-default graphical.target >/dev/null 2>&1 || true
  timeout 8 systemctl disable jukebox-graphical.service >/dev/null 2>&1 || true
  rm -f "$mark" "$updating"
  printf '%s\n' "Videojukebox kom ikke op. Debian-skrivebordet er slaaet til igen." > /dev/console || true
fi

touch "$mark" 2>/dev/null || true
if id jukebox >/dev/null 2>&1; then
  chown jukebox:jukebox "$mark" 2>/dev/null || true
fi
chmod 664 "$mark" 2>/dev/null || true
exit 0
