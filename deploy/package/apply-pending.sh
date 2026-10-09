#!/bin/bash
# Runs at boot, before the jukebox starts.
# Repairs an install that was turned off halfway, then applies one copied package.
set -u
export DEBIAN_FRONTEND=noninteractive

say() {
  printf '%s\n' "$1" > /dev/console || true
}

if grep -q ' half-configured\| half-installed\| reinstreq ' /var/lib/dpkg/status 2>/dev/null; then
  say "Videojukebox gjor en afbrudt installation faerdig. Vent."
  timeout --kill-after=15 90 dpkg --configure -a || true
fi

deb=/var/lib/jukebox/pending/videojukebox.deb
if [[ ! -f "$deb" ]]; then
  exit 0
fi

say "Videojukebox opdaterer. Vent et minut."
if timeout --kill-after=20 180 dpkg -i "$deb"; then
  rm -f "$deb"
  sync
  say "Videojukebox er opdateret."
else
  say "Opdateringen blev ikke faerdig. Maskinen starter alligevel."
fi
timeout --kill-after=15 60 dpkg --configure -a || true
exit 0
