#!/usr/bin/env python3
"""Set mpv's volume, and exit 2 when the file has reached the end."""
import json
import socket
import sys

path, volume = sys.argv[1], sys.argv[2]


def request(sock, payload):
    sock.sendall((json.dumps(payload) + "\n").encode())
    data = b""
    while b"\n" not in data:
        chunk = sock.recv(4096)
        if not chunk:
            break
        data += chunk
    while True:
        line, _, data = data.partition(b"\n")
        if not line:
            break
        message = json.loads(line.decode())
        if "error" in message or "request_id" in message:
            return message
        if b"\n" not in data:
            extra = sock.recv(4096)
            if not extra:
                break
            data += extra
    raise OSError("mpv did not answer")


sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
sock.settimeout(0.4)
sock.connect(path)
def prop(name):
    reply = request(sock, {"command": ["get_property", name]})
    if reply.get("error") not in (None, "success"):
        return None
    return reply.get("data")


try:
    if volume != "-":
        request(sock, {"command": ["set_property", "volume", int(volume)]})
    else:
        eof = prop("eof-reached")
        if eof is True:
            raise SystemExit(2)
        # Idle alone is true before the file starts. Only treat it as the end
        # once playback has already moved.
        idle = prop("idle-active")
        percent = prop("percent-pos")
        if idle is True and isinstance(percent, (int, float)) and percent >= 95:
            raise SystemExit(2)
finally:
    sock.close()
