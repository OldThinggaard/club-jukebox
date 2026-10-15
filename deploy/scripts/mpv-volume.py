#!/usr/bin/env python3
"""Set mpv's volume, and exit 2 when the file has reached the end."""
import json
import os
import socket
import sys

if len(sys.argv) == 2 and sys.argv[1] == "self-test":
    sys.argv.append("-")
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


def active_output(level):
    """Turn the idle jack off and set the one that is already playing.

    Both jacks at once cancel the bass. The headset selects itself, so this
    only follows that choice.
    """
    import subprocess

    level = max(0, min(100, int(level)))
    try:
        inspected = subprocess.run(
            ["wpctl", "inspect", "@DEFAULT_AUDIO_SINK@"],
            capture_output=True,
            text=True,
            timeout=2,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        raise SystemExit(1)
    chosen = ""
    for line in inspected.stdout.splitlines():
        stripped = line.strip()
        if not stripped.startswith("*"):
            continue
        low = stripped.lower()
        if "headphone" in low:
            chosen = "headphone"
            break
        if "speaker" in low:
            chosen = "speaker"
            break
    if not chosen:
        raise SystemExit(1)
    live = "Headphone" if chosen == "headphone" else "Speaker"
    idle_name = "Speaker" if chosen == "headphone" else "Headphone"
    cards = []
    cards_path = os.environ.get("JUKEBOX_CARDS", "/proc/asound/cards")
    if os.path.isfile(cards_path):
        with open(cards_path, encoding="utf-8", errors="replace") as handle:
            for row in handle:
                parts = row.split()
                if parts and parts[0].isdigit():
                    cards.append(parts[0])
    if not cards:
        cards = [None]

    def sset(card, name, value, switch):
        args = ["amixer"]
        if card is not None:
            args += ["-c", card]
        args += ["-q", "sset", name, value, switch]
        try:
            subprocess.run(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=1, check=False)
        except (OSError, subprocess.TimeoutExpired):
            pass

    for card in cards:
        sset(card, live, f"{level}%", "unmute")
        sset(card, idle_name, "0%", "mute")
        sset(card, "Master", f"{level}%", "unmute")


if path == "active":
    active_output(volume)
    raise SystemExit(0)


def reached_end(eof, idle, percent, time_pos, duration):
    # The opening, and a hiccup in the middle, both look like the end.
    # Closing the file there shows the welcome screen and starts the song again.
    del eof, idle, percent
    if time_pos is None or duration is None or duration < 8 or time_pos < 1:
        return False
    return time_pos >= duration - 1.5


if path == "self-test":
    assert reached_end(True, True, 100, None, None) is False
    assert reached_end(True, True, 100, 0, 180) is False
    assert reached_end(True, False, None, 2, 180) is False
    assert reached_end(True, True, 100, 30, 180) is False
    assert reached_end(False, False, None, 10, 180) is False
    assert reached_end(True, False, None, 179, 180) is True
    assert reached_end(False, False, None, 179.6, 180) is True
    raise SystemExit(0)


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
