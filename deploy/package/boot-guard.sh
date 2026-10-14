#!/bin/bash
# A dark screen must not change how Debian starts, and must not restart the PC.
set -u
rm -f /var/lib/jukebox/booting /var/lib/jukebox/recover /var/lib/jukebox/updating
exit 0
