#!/bin/bash
# Installs Videojukebox on a Debian 13 machine. Does not partition or erase a disk.
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Kør den sådan: sudo bash install.sh" >&2
  exit 1
fi

SRC="$(cd "$(dirname "$0")" && pwd)"
if [[ ! -f "$SRC/app/Jukebox.Host" ]]; then
  echo "Mappen app mangler. Pak videojukebox-pakke.zip ud, og kør install.sh inde i mappen." >&2
  exit 1
fi

if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
fi
if [[ "${ID:-}" != "debian" || "${VERSION_ID:-}" != "13" ]]; then
  echo "Denne pakke er til Debian 13. Maskinen kører ${PRETTY_NAME:-et andet system}." >&2
  exit 1
fi

echo "Videojukebox installeres. Disken bliver ikke slettet."
echo "Netværkskablet skal blive siddende, mens pakkerne hentes."

export DEBIAN_FRONTEND=noninteractive
if ! apt-get update; then
  echo "Pakkerne kunne ikke hentes. Sæt netværkskablet i, og kør scriptet igen." >&2
  exit 1
fi

apt-get install -y \
  sudo curl ca-certificates locales \
  network-manager wpasupplicant \
  avahi-daemon \
  xserver-xorg xserver-xorg-legacy xserver-xorg-input-libinput xserver-xorg-video-all \
  xinit x11-xserver-utils \
  openbox unclutter \
  chromium \
  mpv ffmpeg \
  pipewire pipewire-pulse wireplumber \
  alsa-utils \
  python3 \
  pciutils usbutils \
  ntfs-3g \
  fonts-dejavu-core \
  dbus dbus-x11

if ! id jukebox >/dev/null 2>&1; then
  useradd --create-home --home-dir /home/jukebox --shell /bin/bash --user-group jukebox
fi
echo 'jukebox:jukebox' | chpasswd
for group in audio video render input plugdev netdev sudo; do
  if getent group "$group" >/dev/null; then
    usermod -aG "$group" jukebox
  fi
done
install -d -o jukebox -g jukebox /home/jukebox /var/lib/jukebox
printf '%s\n' 'jukebox ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/jukebox
chmod 440 /etc/sudoers.d/jukebox

if [[ -f /etc/locale.gen ]]; then
  sed -i 's/^# *\(da_DK.UTF-8 UTF-8\)/\1/' /etc/locale.gen
fi
grep -q 'da_DK.UTF-8 UTF-8' /etc/locale.gen || echo 'da_DK.UTF-8 UTF-8' >> /etc/locale.gen
locale-gen
update-locale LANG=da_DK.UTF-8
ln -sfn /usr/share/zoneinfo/Europe/Copenhagen /etc/localtime
echo Europe/Copenhagen > /etc/timezone

install -d /opt/jukebox/bin /opt/jukebox/lib /opt/jukebox/updater
rm -rf /opt/jukebox/current
mkdir -p /opt/jukebox/current
cp -a "$SRC/app/." /opt/jukebox/current/
cp -a "$SRC/updater/." /opt/jukebox/updater/
install -m 0755 "$SRC/bin/kiosk.sh" /opt/jukebox/bin/kiosk.sh
install -m 0755 "$SRC/bin/session.sh" /opt/jukebox/bin/session.sh
install -m 0755 "$SRC/lib/onlime-open.sh" "$SRC/lib/onlime-login.sh" "$SRC/lib/onlime-boot.sh" "$SRC/lib/wifi-connect.sh" /opt/jukebox/lib/
install -m 0755 "$SRC/lib/onlime_prompts.py" /opt/jukebox/lib/onlime_prompts.py
chmod 0755 /opt/jukebox/current/Jukebox.Host /opt/jukebox/updater/Jukebox.UpdateAgent
chown -R jukebox:jukebox /opt/jukebox /var/lib/jukebox /home/jukebox

install -d /etc/X11
printf '%s\n' 'allowed_users=anybody' 'needs_root_rights=no' > /etc/X11/Xwrapper.config
install -d /etc/systemd/logind.conf.d
cat > /etc/systemd/logind.conf.d/jukebox.conf <<'EOF'
[Login]
HandleLidSwitch=ignore
HandleLidSwitchExternalPower=ignore
HandleLidSwitchDocked=ignore
IdleAction=ignore
EOF
mkdir -p /etc/systemd/system
for target in sleep.target suspend.target hibernate.target hybrid-sleep.target; do
  ln -sfn /dev/null "/etc/systemd/system/${target}"
done

uid="$(id -u jukebox)"
cat > /etc/systemd/system/jukebox.service <<EOF
[Unit]
Description=Club video jukebox
After=network.target sound.target jukebox-update.service
Wants=avahi-daemon.service
StartLimitIntervalSec=60
StartLimitBurst=10

[Service]
Type=notify
User=jukebox
Group=jukebox
WorkingDirectory=/opt/jukebox/current
Environment=JUKEBOX_DATA_DIR=/var/lib/jukebox
Environment=JUKEBOX_SUPERVISED=1
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:47123
Environment=Jukebox__PublicBaseUrl=http://jukebox.local:47123
Environment=Jukebox__SeedSampleMedia=false
Environment=HOME=/home/jukebox
Environment=DISPLAY=:0
Environment=XAUTHORITY=/home/jukebox/.Xauthority
Environment=XDG_RUNTIME_DIR=/run/user/${uid}
ExecStart=/opt/jukebox/current/Jukebox.Host
Restart=always
RestartSec=2
TimeoutStartSec=300
WatchdogSec=30
MemoryMax=1200M
Nice=-5

[Install]
WantedBy=multi-user.target
EOF

install -m 0644 "$SRC/systemd/jukebox-update.service" /etc/systemd/system/jukebox-update.service

cat > /etc/systemd/system/jukebox-graphical.service <<EOF
[Unit]
Description=Jukebox touchscreen session
After=jukebox.service systemd-user-sessions.service
Wants=jukebox.service
Conflicts=getty@tty1.service

[Service]
User=jukebox
Group=jukebox
PAMName=login
TTYPath=/dev/tty1
StandardInput=tty
StandardOutput=journal
StandardError=journal
UtmpIdentifier=tty1
UtmpMode=user
WorkingDirectory=/home/jukebox
Environment=HOME=/home/jukebox
Environment=DISPLAY=:0
Environment=XAUTHORITY=/home/jukebox/.Xauthority
Environment=XDG_RUNTIME_DIR=/run/user/${uid}
ExecStartPre=+/usr/bin/install -d -o jukebox -g jukebox -m 700 /run/user/${uid}
ExecStart=/usr/bin/xinit /opt/jukebox/bin/session.sh -- /usr/bin/Xorg :0 vt1 -keeptty -nolisten tcp -logfile /home/jukebox/xorg.log
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

cat > /etc/systemd/system/jukebox-onlime.service <<EOF
[Unit]
Description=Onlime sync for the video jukebox
After=network.target jukebox.service

[Service]
Type=oneshot
RemainAfterExit=yes
User=jukebox
Group=jukebox
Environment=HOME=/home/jukebox
Environment=XDG_RUNTIME_DIR=/run/user/${uid}
ExecStartPre=+/usr/bin/install -d -o jukebox -g jukebox -m 700 /run/user/${uid}
ExecStart=/opt/jukebox/lib/onlime-boot.sh

[Install]
WantedBy=multi-user.target
EOF

install -d /etc/avahi/services
install -m 0644 "$SRC/avahi/jukebox.service" /etc/avahi/services/jukebox.service

if [[ -f "$SRC/lib/jotta.gpg" ]]; then
  install -d /etc/apt/keyrings
  install -m 644 "$SRC/lib/jotta.gpg" /etc/apt/keyrings/jotta.gpg
  printf '%s\n' 'deb [signed-by=/etc/apt/keyrings/jotta.gpg] https://repo.jotta.cloud/debian debian main' > /etc/apt/sources.list.d/jotta.list
  if apt-get update && apt-get install -y jotta-cli; then
    echo "Onlime-programmet er installeret."
  else
    echo "Onlime-programmet blev ikke installeret. Jukeboksen virker alligevel. Onlime kan sættes op senere under Administration."
  fi
fi

for dm in gdm gdm3 lightdm sddm; do
  systemctl disable "$dm" >/dev/null 2>&1 || true
done
systemctl set-default multi-user.target
systemctl daemon-reload
systemctl enable avahi-daemon.service
systemctl enable NetworkManager.service
systemctl enable jukebox-update.service
systemctl enable jukebox.service
systemctl enable jukebox-graphical.service
systemctl enable jukebox-onlime.service

echo
echo "Videojukebox er installeret."
echo "Tag USB-nøglen ud, og skriv: reboot"
echo "Efter genstart viser touchskærmen hylden. HDMI-tv'et viser filmene."
echo "Administration: PIN 2580. Fanen netværk og fanen onlime bruges bagefter."
echo "Konsol, hvis billedet driller: Ctrl+Alt+F2, bruger jukebox, adgangskode jukebox."
