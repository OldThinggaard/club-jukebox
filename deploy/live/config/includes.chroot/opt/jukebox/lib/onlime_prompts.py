#!/usr/bin/env python3
"""Answer Onlime's prompts in the order version 0.17 prints them.

The login token is written to the program and never printed back.
"""
import os
import select
import sys
import time


def answer(mode, pending, sent):
    """Return the next reply and the prompt it matched, or (None, None)."""
    low = pending.lower()
    if mode == "login":
        if "license" not in sent and "accept license" in low:
            return "yes", "accept license"
        if "token" not in sent and "license" in sent and "enter your personal login token" in low:
            return "TOKEN", "enter your personal login token"
        if "token" not in sent and "license" in sent and "personal login token:" in low:
            return "TOKEN", "personal login token:"
        if "device" not in sent and "token" in sent and "name to identify this device" in low:
            return "DEVICE", "name to identify this device"
        if "device" not in sent and "token" in sent and "choose a device name" in low:
            return "DEVICE", "choose a device name"
        if "device" not in sent and "token" in sent and "devicename" in low:
            return "DEVICE", "devicename"
        if "reuse" not in sent and "re-use this device" in low:
            return "yes", "re-use this device"
        return None, None
    if "continue" not in sent and "continue sync setup?" in low:
        return "yes", "continue sync setup?"
    if "report" not in sent and "continue" in sent and "error reporting mode" in low:
        return "off", "error reporting mode"
    if "path" not in sent and "report" in sent and "continue sync-setup" in low:
        return "y", "continue sync-setup"
    if "selective" not in sent and "path" in sent and "setup selective sync?" in low:
        return "n", "setup selective sync?"
    return None, None


def mark(mode, reply, sent):
    if mode == "login":
        if reply == "yes" and "license" not in sent and "reuse" not in sent:
            sent.add("license")
        elif reply == "yes":
            sent.add("reuse")
        elif reply == "TOKEN":
            sent.add("token")
        elif reply == "DEVICE":
            sent.add("device")
        return
    if reply == "yes":
        sent.add("continue")
    elif reply == "off":
        sent.add("report")
    elif reply == "y":
        sent.add("path")
    elif reply == "n":
        sent.add("selective")


def spawn(argv):
    pid, fd = os.forkpty()
    if pid == 0:
        os.execvp(argv[0], argv)
    return pid, fd


def reply_to(fd, text):
    os.write(fd, (text + "\n").encode())


def self_test():
    sent = set()
    cases = [
        ("login", "accept license (yes/no)", "yes"),
        ("login", "Enter your personal login token.", "TOKEN"),
        ("login", "Enter a name to identify this device.", "DEVICE"),
    ]
    for mode, text, expect in cases:
        got, _marker = answer(mode, text, sent)
        if got != expect:
            raise SystemExit(f"{text!r} answered {got!r}, expected {expect!r}")
        mark(mode, got, sent)
    sent = set()
    screen = "Logging into jottacli is done using a Personal login token.\naccept license (yes/no)"
    if answer("login", screen, sent)[0] != "yes":
        raise SystemExit("the license screen must be accepted before the token is sent")
    sent = set()
    sync = [
        ("Continue sync setup? [yes]", "yes"),
        ("Choose error reporting mode[full]", "off"),
        ("continue sync-setup(y/n)", "y"),
        ("Do you want to setup selective sync? (y/n)", "n"),
    ]
    for text, expect in sync:
        got, _marker = answer("sync", text, sent)
        if got != expect:
            raise SystemExit(f"{text!r} answered {got!r}, expected {expect!r}")
        mark("sync", got, sent)
    print("ok")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "self-test":
        self_test()
        return
    mode = sys.argv[1]
    token = ""
    device = ""
    if mode == "login":
        token = open(sys.argv[2], encoding="utf-8").read().strip()
        device = sys.argv[3]
        pid, fd = spawn(["jotta-cli", "login"])
    elif mode == "sync":
        pid, fd = spawn(["jotta-cli", "sync", "setup", "--root", sys.argv[2]])
    else:
        sys.exit(2)

    sent = set()
    pending = ""
    deadline = time.time() + 90
    status = 1
    while time.time() < deadline:
        ready, _, _ = select.select([fd], [], [], 1)
        if ready:
            try:
                chunk = os.read(fd, 4096)
            except OSError:
                chunk = b""
            if not chunk:
                break
            text = chunk.decode("utf-8", "replace")
            safe = text.replace(token, "••••") if token else text
            sys.stdout.write(safe)
            sys.stdout.flush()
            pending += safe
            if len(pending) > 8000:
                pending = pending[-8000:]
            while True:
                chosen, marker = answer(mode, pending, sent)
                if not chosen or not marker:
                    break
                value = token if chosen == "TOKEN" else device if chosen == "DEVICE" else chosen
                reply_to(fd, value)
                mark(mode, chosen, sent)
                at = pending.lower().find(marker)
                pending = pending[at + len(marker):] if at >= 0 else ""
        ended, code = os.waitpid(pid, os.WNOHANG)
        if ended:
            status = os.waitstatus_to_exitcode(code)
            break
    else:
        try:
            os.kill(pid, 15)
        except OSError:
            pass
        status = 1
    if mode == "login" and "token" not in sent and status == 0:
        if "already logged in" not in pending.lower():
            status = 1
    if "authentication failed" in pending.lower() or "not logged in" in pending.lower():
        status = 1
    sys.exit(status)


if __name__ == "__main__":
    main()
