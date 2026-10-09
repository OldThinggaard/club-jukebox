#!/bin/bash
# Install Onlime Sync (jotta-cli). Does nothing when it is already present.
set -euo pipefail
if [[ -x /usr/bin/jotta-cli ]]; then
  echo "Onlime Sync er installeret."
  exit 0
fi
if [[ ! -f /usr/share/jukebox/jotta.gpg ]]; then
  echo "Nøglen til Onlime mangler." >&2
  exit 1
fi
install -d /etc/apt/keyrings
install -m 644 /usr/share/jukebox/jotta.gpg /etc/apt/keyrings/jotta.gpg
printf '%s\n' 'deb [signed-by=/etc/apt/keyrings/jotta.gpg] https://repo.jotta.cloud/debian debian main' > /etc/apt/sources.list.d/jotta.list
export DEBIAN_FRONTEND=noninteractive
opts=( -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 )
if ! apt-get "${opts[@]}" update; then
  echo "Onlime Sync kunne ikke hentes. Sæt netværkskablet i, og prøv igen." >&2
  exit 1
fi
if ! apt-get "${opts[@]}" install -y jotta-cli; then
  echo "Onlime Sync kunne ikke hentes. Sæt netværkskablet i, og prøv igen." >&2
  exit 1
fi
echo "Onlime Sync er installeret."
