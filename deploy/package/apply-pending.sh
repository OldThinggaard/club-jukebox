#!/bin/bash
# Install one copied package. Never on the critical boot path, and never killed.
# A killed dpkg is what forces a Debian reinstall.
set -u
export DEBIAN_FRONTEND=noninteractive
deb=/var/lib/jukebox/pending/videojukebox.deb
updating=/var/lib/jukebox/updating
if [[ ! -f "$deb" ]]; then
  exit 0
fi

install -d /var/lib/jukebox/failed-updates
printf '%s\n' "Videojukebox opdaterer. Lad maskinen vaere." > /dev/console || true
printf '%s\n' "1" > "$updating"
sync

if dpkg -i "$deb"; then
  rm -f "$deb" "$updating" /var/lib/jukebox/update-error
  sync
  printf '%s\n' "Videojukebox er opdateret. Lad maskinen vaere." > /dev/console || true
  exit 0
fi

rm -f "$updating"
mv "$deb" "/var/lib/jukebox/failed-updates/videojukebox-$(date +%s).deb" 2>/dev/null || true
printf '%s\n' "Opdateringen fejlede. Den koerende version bliver." > /var/lib/jukebox/update-error
printf '%s\n' "Opdateringen fejlede. Maskinen bliver paa den gamle version." > /dev/console || true
exit 0
