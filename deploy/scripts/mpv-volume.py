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


def read_clock():
    """Longest length seen for this title, and how many end readings are in a row."""
    try:
        with open(clock_path, encoding="utf-8") as handle:
            lines = [line.strip() for line in handle.readlines()]
    except OSError:
        return 0.0, 0
    if not lines or lines[0] != queue_id:
        return 0.0, 0
    longest = 0.0
    streak = 0
    if len(lines) >= 3:
        try:
            longest = max(0.0, int(lines[2]) / 1000)
        except ValueError:
            longest = 0.0
    if len(lines) >= 5:
        try:
            streak = max(0, int(lines[4]))
        except ValueError:
            streak = 0
    return longest, streak


def write_clock(time_pos, longest, ended, streak):
    if not queue_id:
        return
    time_ms = int(time_pos * 1000) if time_pos is not None else 0
    duration_ms = int(longest * 1000) if longest else 0
    text = f"{queue_id}\n{time_ms}\n{duration_ms}\n{1 if ended else 0}\n{streak}\n"
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


def consider_end(eof, time_pos, duration, previous_longest, streak):
    """Confirm the end only when the file itself is finished.

    One short length, or a length that shrinks, does not count. The position
    has to sit at the longest length seen so far, and the player has to say
    the file is over, twice in a row.
    """
    reported = duration if duration is not None and duration > 0 else 0.0
    position = time_pos if time_pos is not None and time_pos > 0 else 0.0
    longest = max(previous_longest, reported)
    agrees = reported == 0 or reported >= longest - 1
    near = longest >= 1 and position >= longest - 0.5 and agrees
    if eof is True and near:
        streak += 1
    else:
        streak = 0
    return longest, streak, streak >= 2


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
        previous, streak = read_clock()
        longest, streak, ended = consider_end(eof, time_pos, duration, previous, streak)
        write_clock(time_pos, longest, ended, streak)
        if ended:
            raise SystemExit(2)
finally:
    sock.close()
