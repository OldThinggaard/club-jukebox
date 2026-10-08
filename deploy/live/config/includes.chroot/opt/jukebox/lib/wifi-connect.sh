#!/usr/bin/env python3
"""Save a Wi-Fi password and connect. Reads ssid and psk from a private directory."""
import pathlib
import subprocess
import sys

folder = pathlib.Path(sys.argv[1])
ssid = (folder / "ssid").read_text(encoding="utf-8").removesuffix("\n")
psk_path = folder / "psk"
psk = psk_path.read_text(encoding="utf-8").removesuffix("\n") if psk_path.exists() else ""
if not ssid or "\n" in ssid or "\r" in ssid or "\n" in psk or "\r" in psk:
    sys.exit("Netværksnavnet er ugyldigt.")

security = ""
if psk:
    security = f"[wifi-security]\nkey-mgmt=wpa-psk\npsk={psk}\n"

text = f"""[connection]
id=jukebox-wifi
type=wifi
autoconnect=true

[wifi]
mode=infrastructure
ssid={ssid}

{security}[ipv4]
method=auto

[ipv6]
method=auto
"""
destination = pathlib.Path("/etc/NetworkManager/system-connections/jukebox-wifi.nmconnection")
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(text, encoding="utf-8")
destination.chmod(0o600)
subprocess.check_call(["nmcli", "connection", "reload"])
subprocess.check_call(["nmcli", "connection", "up", "jukebox-wifi"])
print(f"Forbundet til {ssid}")
