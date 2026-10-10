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
try:
    if volume != "-":
        request(sock, {"command": ["set_property", "volume", int(volume)]})
    reply = request(sock, {"command": ["get_property", "eof-reached"]})
    if reply.get("data") is True:
        raise SystemExit(2)
finally:
    sock.close()
