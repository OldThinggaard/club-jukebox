#!/usr/bin/env python3
"""Set mpv's volume, and exit 2 when the file has reached the end."""
import json
import os
import socket
import sys

path, volume = sys.argv[1], sys.argv[2]
queue_id = sys.argv[3] if len(sys.argv) > 3 else ""
clock_path = "/var/lib/jukebox/play.clock"


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


def prop(sock, name):
    reply = request(sock, {"command": ["get_property", name]})
    if reply.get("error") not in (None, "success"):
        return None
    return reply.get("data")


def number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if value != value or value in (float("inf"), float("-inf")):
        return None
    return float(value)


def write_clock(time_pos, duration, ended):
    if not queue_id:
        return
    time_ms = int(time_pos * 1000) if time_pos is not None else 0
    duration_ms = int(duration * 1000) if duration is not None else 0
    text = f"{queue_id}\n{time_ms}\n{duration_ms}\n{1 if ended else 0}\n"
    os.makedirs(os.path.dirname(clock_path), exist_ok=True)
    temporary = clock_path + ".tmp"
    with open(temporary, "w", encoding="utf-8") as handle:
        handle.write(text)
    os.replace(temporary, clock_path)


def reached_end(eof, idle, percent, time_pos, duration):
    if eof is True:
        return True
    if duration is not None and duration >= 1 and time_pos is not None and time_pos >= duration - 0.5:
        return True
    if idle is True and isinstance(percent, (int, float)) and not isinstance(percent, bool) and percent >= 95:
        return True
    return False


sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
sock.settimeout(0.4)
sock.connect(path)
try:
    if volume != "-":
        level = max(0, min(100, int(volume)))
        reply = request(sock, {"command": ["set_property", "volume", level]})
        if reply.get("error") not in (None, "success"):
            raise SystemExit(1)
    else:
        eof = prop(sock, "eof-reached")
        time_pos = number(prop(sock, "time-pos"))
        duration = number(prop(sock, "duration"))
        idle = prop(sock, "idle-active")
        percent = prop(sock, "percent-pos")
        ended = reached_end(eof, idle, percent, time_pos, duration)
        write_clock(time_pos, duration, ended)
        if ended:
            raise SystemExit(2)
finally:
    sock.close()
