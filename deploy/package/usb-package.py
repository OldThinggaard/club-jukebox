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
    if os.path.basename(path).lower() != NAME or os.path.islink(path) or not os.path.isfile(path):
        return False
    real = os.path.realpath(path)
    if os.path.basename(real).lower() != NAME:
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
            for filename in filenames:
                if filename.lower() != NAME:
                    continue
                path = os.path.join(dirpath, filename)
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


def normalize_fs(fstype):
    kind = (fstype or "").strip().lower()
    if kind in {"fat", "fat32", "msdos"}:
        return "vfat"
    if kind in {"ntfs3", "fuseblk"}:
        return "ntfs"
    return kind


def filesystem(name, hinted):
    kind = normalize_fs(hinted)
    if kind:
        return kind
    device = os.path.join("/dev", name)
    if not name or "/" in name or not os.path.exists(device):
        return ""
    try:
        out = subprocess.check_output(
            ["blkid", "-o", "value", "-s", "TYPE", device],
            text=True,
            stderr=subprocess.DEVNULL,
        ).strip()
    except (OSError, subprocess.CalledProcessError):
        return ""
    return normalize_fs(out)


def mount_types(fstype):
    if fstype == "exfat":
        subprocess.run(["modprobe", "exfat"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return ["exfat"]
    if fstype == "ntfs":
        subprocess.run(["modprobe", "ntfs3"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return ["ntfs3", "ntfs"]
    return [fstype]


def jukebox_ids():
    try:
        import pwd

        user = pwd.getpwnam("jukebox")
    except (ImportError, KeyError):
        return "", ""
    return str(user.pw_uid), str(user.pw_gid)


def owner_option(fstype, uid, gid):
    base = "ro,nodev,nosuid,noexec"
    if fstype in {"vfat", "ntfs", "exfat"} and uid and gid:
        return f"{base},uid={uid},gid={gid}"
    return base


def mount_one(name, fstype):
    if not name or "/" in name or name.startswith("."):
        return False
    target = os.path.join(MOUNT_PARENT, name)
    os.makedirs(target, exist_ok=True)
    device = os.path.join("/dev", name)
    if not os.path.exists(device):
        return False
    uid, gid = jukebox_ids()
    options = [owner_option(fstype, uid, gid)]
    plain = "ro,nodev,nosuid,noexec"
    if options[0] != plain:
        options.append(plain)
    for kind in mount_types(fstype):
        for opt in options:
            result = subprocess.run(
                ["timeout", "15", "mount", "-t", kind, "-o", opt, device, target],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            if result.returncode == 0:
                return True
    return False


def visit(node, root_disk, state, parent_removable=False):
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
    children = node.get("children") or []
    kind = filesystem(name, node.get("fstype")) if removable else ""
    volume = node.get("type") == "part" or (node.get("type") == "disk" and not children)
    if volume and removable and kind in ALLOWED_FS:
        if mounted or mount_one(name, kind):
            state["mounted"] = True
        else:
            state["failed"] = True
    for child in children:
        visit(child, root_disk, state, removable)


def mount_removable(state):
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
        visit(device, root_disk, state)


def report(path, state):
    if path:
        return f"ready {path}"
    if state.get("mounted"):
        return "seen"
    if state.get("failed"):
        return "unread"
    return "none"


def install():
    mount_removable({"mounted": False, "failed": False})
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
    # Copy first, then install while the machine is already up.
    # dpkg is not run from early boot, and it is not killed.
    os.makedirs("/var/lib/jukebox/pending", exist_ok=True)
    local = "/var/lib/jukebox/pending/videojukebox.deb"
    copied = subprocess.run(["cp", "-f", path, local], stdout=log, stderr=log, check=False)
    if copied.returncode != 0 or not os.path.isfile(local):
        print("could not copy the package off the USB stick", file=log, flush=True)
        return copied.returncode or 1
    subprocess.run(["sync"], check=False, timeout=20)
    script = "/opt/jukebox/lib/apply-pending.sh"
    if not os.path.isfile(script):
        print("apply-pending.sh is missing", file=log, flush=True)
        return 1
    os.execv(script, [script])


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
        windows_name = os.path.join(stick, "Videojukebox.deb")
        os.rename(big, windows_name)
        found = find_deb()
        if os.path.realpath(found) != os.path.realpath(windows_name):
            raise SystemExit(f"expected the Windows name, got {found!r}")
        os.rename(windows_name, big)
        if report("/mnt/jukebox-usb/sdb1/videojukebox.deb", {}) != "ready /mnt/jukebox-usb/sdb1/videojukebox.deb":
            raise SystemExit("ready line")
        if report("", {"mounted": True}) != "seen":
            raise SystemExit("seen line")
        if report("", {"failed": True}) != "unread":
            raise SystemExit("unread line")
        if report("", {}) != "none":
            raise SystemExit("none line")
        if normalize_fs("NTFS3") != "ntfs" or normalize_fs("ExFAT") != "exfat" or normalize_fs("FAT32") != "vfat":
            raise SystemExit("windows filesystem names")
        if owner_option("vfat", "1000", "1000") != "ro,nodev,nosuid,noexec,uid=1000,gid=1000":
            raise SystemExit("windows stick must be readable by the jukebox user")
        if owner_option("ext4", "1000", "1000") != "ro,nodev,nosuid,noexec":
            raise SystemExit("ext4 keeps its own owners")
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
        mount_removable({"mounted": False, "failed": False})
        return 0
    if command == "find":
        state = {"mounted": False, "failed": False}
        mount_removable(state)
        print(report(find_deb(), state))
        return 0
    if command == "install":
        return install()
    print("unknown command", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
