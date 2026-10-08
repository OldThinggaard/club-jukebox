#!/usr/bin/env bash
# Installs the jukebox onto a Debian machine. Run as root after deploy/scripts/publish.sh.
set -euo pipefail
if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root." >&2
  exit 1
fi
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ ! -x "$ROOT/publish/app/Jukebox.Host" ]]; then
  echo "Missing publish/app. Run deploy/scripts/publish.sh first." >&2
  exit 1
fi

apt-get update
apt-get install -y mpv ffmpeg chromium avahi-daemon x11-xserver-utils pulseaudio-utils || apt-get install -y mpv ffmpeg chromium-browser avahi-daemon x11-xserver-utils

id jukebox >/dev/null 2>&1 || useradd --system --create-home --home-dir /var/lib/jukebox --shell /usr/sbin/nologin jukebox
install -d -o jukebox -g jukebox /opt/jukebox/current /opt/jukebox/previous /opt/jukebox/updater /opt/jukebox/bin /var/lib/jukebox
rm -rf /opt/jukebox/current
cp -a "$ROOT/publish/app" /opt/jukebox/current
cp -a "$ROOT/publish/updater/." /opt/jukebox/updater/
install -m 0755 "$ROOT/deploy/scripts/kiosk.sh" /opt/jukebox/bin/kiosk.sh
chown -R jukebox:jukebox /opt/jukebox /var/lib/jukebox

install -m 0644 "$ROOT/deploy/systemd/jukebox.service" /etc/systemd/system/jukebox.service
install -m 0644 "$ROOT/deploy/systemd/jukebox-update.service" /etc/systemd/system/jukebox-update.service
install -m 0644 "$ROOT/deploy/systemd/jukebox-kiosk.service" /etc/systemd/system/jukebox-kiosk.service
install -m 0644 "$ROOT/deploy/systemd/jukebox-hall.service" /etc/systemd/system/jukebox-hall.service
install -d /etc/avahi/services
install -m 0644 "$ROOT/deploy/avahi/jukebox.service" /etc/avahi/services/jukebox.service

# Production should advertise the LAN name. The value is kept if the club already changed it.
sudo -u jukebox bash -lc 'test -f /var/lib/jukebox/jukebox.db || true'

systemctl daemon-reload
systemctl enable --now avahi-daemon
systemctl enable --now jukebox-update.service
systemctl enable --now jukebox.service
echo "Jukebox service is enabled. Enable jukebox-kiosk and jukebox-hall after the graphical session exists."
echo "Open http://jukebox.local:47123/  and  http://127.0.0.1:47123/player"
