#!/usr/bin/env python3
"""Save a Wi-Fi password and connect. Reads ssid and psk from a private directory."""
import pathlib
import subprocess
import sys

def keyfile_escape(value: str) -> str:
    """Keep every sign in a NetworkManager keyfile, including # and spaces."""
    parts = []
    for ch in value:
        if ch == "\\":
            parts.append("\\\\")
        elif ch == " ":
            parts.append("\\s")
        elif ch == "\t":
            parts.append("\\t")
        elif ch == "\n":
            parts.append("\\n")
        elif ch == "\r":
            parts.append("\\r")
        else:
            parts.append(ch)
    return "".join(parts)


if len(sys.argv) == 2 and sys.argv[1] == "self-test":
    sample = "a!b#c d\\e"
    escaped = keyfile_escape(sample)
    if "!" not in escaped or "#" not in escaped or "\\s" not in escaped or "\\\\" not in escaped:
        sys.exit("The Wi-Fi password lost a sign.")
    sys.exit(0)

folder = pathlib.Path(sys.argv[1])
ssid = (folder / "ssid").read_text(encoding="utf-8").removesuffix("\n")
psk_path = folder / "psk"
psk = psk_path.read_text(encoding="utf-8").removesuffix("\n") if psk_path.exists() else ""
if not ssid or "\n" in ssid or "\r" in ssid or "\n" in psk or "\r" in psk:
    sys.exit("Netværksnavnet er ugyldigt.")

security = ""
if psk:
    security = f"[wifi-security]\nkey-mgmt=wpa-psk\npsk={keyfile_escape(psk)}\n"

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
