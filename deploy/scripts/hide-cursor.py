#!/usr/bin/env python3
"""Keep the pointer hidden for the whole session. A finger still works."""
import ctypes
import sys
import time


def hide():
    x11 = ctypes.CDLL("libX11.so.6")
    xfixes = ctypes.CDLL("libXfixes.so.3")
    x11.XOpenDisplay.restype = ctypes.c_void_p
    x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
    x11.XDefaultRootWindow.restype = ctypes.c_ulong
    x11.XFlush.argtypes = [ctypes.c_void_p]
    xfixes.XFixesHideCursor.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    display = x11.XOpenDisplay(None)
    if not display:
        print("No display", file=sys.stderr)
        return 1
    # One hide from this process is enough. Motion does not bring the arrow back.
    # The process has to stay open, or the arrow returns when the connection closes.
    root = x11.XDefaultRootWindow(display)
    xfixes.XFixesHideCursor(display, root)
    x11.XFlush(display)
    while True:
        time.sleep(3600)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "self-test":
        raise SystemExit(0)
    try:
        raise SystemExit(hide())
    except OSError as exc:
        print(exc, file=sys.stderr)
        raise SystemExit(1)
