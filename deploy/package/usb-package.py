#!/usr/bin/env python3
"""Find videojukebox.deb on a removable disk, and install it.

Only partitions marked removable are mounted, and never the disk that holds /.
"""
import json
import os
import subprocess
import sys

MIN_BYTES = 8_000_000
MAX_BYTES = 400_000_000
NAME = "videojukebox.deb"
DEFAULT_ROOTS = ("/media", "/run/media", "/mnt/jukebox-usb")
MOUNT_PARENT = "/mnt/jukebox-usb"
ALLOWED_FS = {"vfat", "ntfs", "exfat", "ext2", "ext3", "ext4"}
SKIP_DISKS = {"vda", "vdb"}


def roots():
    extra = os.environ.get("JUKEBOX_USB_ROOTS", "")
    if extra:
        return tuple(part for part in extra.split(":") if part)
    return DEFAULT_ROOTS


def real_roots():
    found = []
    for root in roots():
        if os.path.isdir(root):
            found.append(os.path.realpath(root))
    return found


def accept(path):
    if os.path.basename(path) != NAME or os.path.islink(path) or not os.path.isfile(path):
        return False
    real = os.path.realpath(path)
    if os.path.basename(real) != NAME:
        return False
    inside = False
    for root in real_roots():
        if real == root or real.startswith(root + os.sep):
            inside = True
            break
    if not inside:
        return False
    size = os.path.getsize(real)
    return MIN_BYTES <= size <= MAX_BYTES


def find_deb():
    matches = []
    for root in roots():
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
            dirnames[:] = [
                name
                for name in dirnames
                if not name.startswith(".") and not os.path.islink(os.path.join(dirpath, name))
            ]
            if NAME not in filenames:
                continue
            path = os.path.join(dirpath, NAME)
            if accept(path):
                matches.append(os.path.realpath(path))
    matches.sort()
    return matches[0] if matches else ""


def system_disk():
    try:
        source = subprocess.check_output(["findmnt", "-n", "-o", "SOURCE", "/"], text=True).strip()
    except (OSError, subprocess.CalledProcessError):
        return ""
    if not source:
        return ""
    try:
        pk = subprocess.check_output(["lsblk", "-no", "PKNAME", source], text=True).strip().splitlines()
    except (OSError, subprocess.CalledProcessError):
        return ""
    return pk[0] if pk else ""


def truthy(value):
    return value in (True, 1, "1")


def mount_one(name):
    if not name or "/" in name or name.startswith("."):
        return
    target = os.path.join(MOUNT_PARENT, name)
    os.makedirs(target, exist_ok=True)
    device = os.path.join("/dev", name)
    if not os.path.exists(device):
        return
    subprocess.run(
        ["mount", "-o", "ro,nodev,nosuid,noexec", device, target],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def visit(node, root_disk, parent_removable=False):
    name = node.get("name") or ""
    if name in SKIP_DISKS or (root_disk and name == root_disk):
        return
    pk = node.get("pkname") or ""
    if pk in SKIP_DISKS or (root_disk and pk == root_disk):
        return
    removable = parent_removable or truthy(node.get("rm"))
    mounts = node.get("mountpoints") or []
    if isinstance(mounts, str):
        mounts = [mounts]
    mounted = any(point for point in mounts if point)
    fstype = (node.get("fstype") or "").lower()
    if node.get("type") == "part" and removable and not mounted and fstype in ALLOWED_FS:
        mount_one(name)
    for child in node.get("children") or []:
        visit(child, root_disk, removable)


def mount_removable():
    if os.environ.get("JUKEBOX_USB_NO_MOUNT") == "1":
        return
    try:
        raw = subprocess.check_output(
            ["lsblk", "-J", "-o", "NAME,RM,TYPE,PKNAME,FSTYPE,MOUNTPOINTS"],
            text=True,
        )
    except (OSError, subprocess.CalledProcessError):
        return
    devices = json.loads(raw).get("blockdevices") or []
    root_disk = system_disk()
    os.makedirs(MOUNT_PARENT, exist_ok=True)
    for device in devices:
        visit(device, root_disk)


def install():
    mount_removable()
    path = find_deb()
    if not path:
        print("Ingen videojukebox.deb på USB-nøglen.", file=sys.stderr)
        return 1
    log_path = "/var/log/jukebox-usb-install.log"
    try:
        log = open(log_path, "a", encoding="utf-8")
    except OSError:
        log = sys.stderr
    print(f"install {path}", file=log, flush=True)
    env = os.environ.copy()
    env["DEBIAN_FRONTEND"] = "noninteractive"
    apt = subprocess.run(["apt-get", "install", "-y", path], env=env, stdout=log, stderr=log)
    if apt.returncode != 0:
        print("install failed", file=log, flush=True)
        return apt.returncode
    subprocess.run(["sync"], check=False)
    subprocess.run(["systemctl", "reboot"], check=False)
    return 0


def self_test():
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        os.environ["JUKEBOX_USB_ROOTS"] = tmp
        os.environ["JUKEBOX_USB_NO_MOUNT"] = "1"
        small = os.path.join(tmp, NAME)
        with open(small, "wb") as handle:
            handle.write(b"x" * 64)
        if find_deb():
            raise SystemExit("a tiny file must be ignored")
        os.remove(small)
        stick = os.path.join(tmp, "stick")
        os.makedirs(stick)
        big = os.path.join(stick, NAME)
        with open(big, "wb") as handle:
            handle.truncate(MIN_BYTES)
        found = find_deb()
        if os.path.realpath(found) != os.path.realpath(big):
            raise SystemExit(f"expected the package, got {found!r}")
        outside = os.path.join(tempfile.gettempdir(), "jukebox-usb-outside-payload")
        with open(outside, "wb") as handle:
            handle.truncate(MIN_BYTES)
        try:
            linked = os.path.join(tmp, "linked")
            os.makedirs(linked, exist_ok=True)
            link = os.path.join(linked, NAME)
            os.symlink(outside, link)
            os.remove(big)
            if find_deb():
                raise SystemExit("a link out of the USB tree must be ignored")
        finally:
            os.remove(outside)
    print("ok")
    return 0


def main(argv):
    command = argv[1] if len(argv) > 1 else "find"
    if command == "self-test":
        return self_test()
    if command == "mount":
        mount_removable()
        return 0
    if command == "find":
        mount_removable()
        print(find_deb())
        return 0
    if command == "install":
        return install()
    print("unknown command", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
