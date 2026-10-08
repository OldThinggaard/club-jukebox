#!/usr/bin/env python3
"""Drive the interactive Onlime CLI. The login token is sent on the prompt, never printed back."""
import os
import select
import sys
import time


def spawn(argv):
    pid, fd = os.forkpty()
    if pid == 0:
        os.execvp(argv[0], argv)
    return pid, fd


def reply(fd, text):
    os.write(fd, (text + "\n").encode())


def main():
    mode = sys.argv[1]
    if mode == "login":
        token = open(sys.argv[2], encoding="utf-8").read().strip()
        device = sys.argv[3]
        pid, fd = spawn(["jotta-cli", "login"])
    elif mode == "sync":
        token = ""
        device = ""
        pid, fd = spawn(["jotta-cli", "sync", "setup", "--root", sys.argv[2]])
    else:
        sys.exit(2)

    sent = set()
    transcript = []
    deadline = time.time() + 120
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
            tail = safe.lower()
            if mode == "login":
                if "token" not in sent and "token" in tail:
                    reply(fd, token)
                    sent.add("token")
                elif "device" not in sent and "token" in sent and ("device" in tail or "name" in tail):
                    reply(fd, device)
                    sent.add("device")
                elif "terms" not in sent and any(word in tail for word in ("terms", "accept", "agree", "betingelse")):
                    reply(fd, "yes")
                    sent.add("terms")
            else:
                if "report" not in sent and "report" in tail:
                    reply(fd, "off")
                    sent.add("report")
                elif "selective" not in sent and "selective" in tail:
                    reply(fd, "n")
                    sent.add("selective")
                elif "disclaimer" not in sent and any(word in tail for word in ("disclaimer", "agree", "betingelse", "yes")):
                    reply(fd, "yes")
                    sent.add("disclaimer")
                elif "path" not in sent and any(word in tail for word in ("correct", "y/n", "(y/n)")):
                    reply(fd, "y")
                    sent.add("path")
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
    sys.exit(status)


if __name__ == "__main__":
    main()
