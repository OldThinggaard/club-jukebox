#!/bin/bash
# Runs at boot, before the jukebox starts.
# Repairs an install that was turned off halfway, then applies one copied package.
set -u
export DEBIAN_FRONTEND=noninteractive

if grep -q ' half-configured\| half-installed\| reinstreq ' /var/lib/dpkg/status 2>/dev/null; then
  timeout 90 dpkg --configure -a || true
fi

deb=/var/lib/jukebox/pending/videojukebox.deb
if [[ ! -f "$deb" ]]; then
  exit 0
fi

timeout 120 dpkg -i "$deb" || true
timeout 60 dpkg --configure -a || true
rm -f "$deb"
sync
exit 0
