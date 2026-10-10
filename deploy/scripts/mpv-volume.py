#!/usr/bin/env python3
"""Set the volume on the running mpv. The slider calls this while a song plays."""
import socket
import sys

path, volume = sys.argv[1], sys.argv[2]
number = int(volume)
sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
sock.settimeout(0.4)
try:
    sock.connect(path)
    sock.sendall(b'{"command":["set_property","volume",%d]}\n' % number)
finally:
    sock.close()
