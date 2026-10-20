#!/usr/bin/env python3
"""Install videojukebox.deb onto the Debian disk that already runs the jukebox.

The script never formats a disk. It only mounts a filesystem that already
contains the installed program, then asks dpkg to replace that program.
"""
import json
import os
import shutil
import subprocess
import sys
import time

DEB = os.environ.get("JUKEBOX_UPDATE_DEB", "/opt/jukebox/lib/videojukebox.deb")
ACCEPTED_FS = {"ext2", "ext3", "ext4", "btrfs", "xfs"}
SKIP_DISKS = {"vda", "vdb"}


def say(text):
    print(text, flush=True)
    try:
        with open("/dev/tty1", "a", encoding="utf-8", errors="replace") as tty:
            tty.write(text + "\n")
            tty.flush()
    except OSError:
        pass


def live_disks():
    found = set(SKIP_DISKS)
    for target in ("/", "/run/live/medium", "/run/live/findiso", "/lib/live/mount/medium"):
        source = findmnt_source(target)
        if not source:
            continue
        name = os.path.basename(source)
        found.add(name)
        parent = pkname(source)
        if parent:
            found.add(parent)
    return found


def findmnt_source(target):
    try:
        out = subprocess.check_output(["findmnt", "-n", "-o", "SOURCE", target], text=True, stderr=subprocess.DEVNULL)
    except (OSError, subprocess.CalledProcessError):
        return ""
    return out.strip().splitlines()[0] if out.strip() else ""


def pkname(source):
    try:
        out = subprocess.check_output(["lsblk", "-no", "PKNAME", source], text=True, stderr=subprocess.DEVNULL)
    except (OSError, subprocess.CalledProcessError):
        return ""
    lines = [line.strip() for line in out.splitlines() if line.strip()]
    return lines[0] if lines else ""


def block_devices():
    try:
        raw = subprocess.check_output(
            ["lsblk", "-J", "-o", "NAME,PATH,TYPE,FSTYPE,PKNAME,MOUNTPOINTS"],
            text=True,
        )
    except (OSError, subprocess.CalledProcessError):
        return []
    return json.loads(raw).get("blockdevices") or []


def flatten(nodes, parent=""):
    rows = []
    for node in nodes:
        name = node.get("name") or ""
        pk = node.get("pkname") or parent
        rows.append({
            "name": name,
            "path": node.get("path") or (f"/dev/{name}" if name else ""),
            "type": node.get("type") or "",
            "fstype": (node.get("fstype") or "").lower(),
            "pkname": pk,
            "mounts": mounts_of(node),
        })
        rows.extend(flatten(node.get("children") or [], name if node.get("type") == "disk" else pk))
    return rows


def mounts_of(node):
    mounts = node.get("mountpoints") or []
    if isinstance(mounts, str):
        return [mounts] if mounts else []
    return [point for point in mounts if point]


def skipped_mount(mounts):
    for point in mounts:
        if point in {"/", "/cdrom"} or point.startswith("/run/live") or point.startswith("/lib/live"):
            return True
    return False


def candidates(rows, busy_disks):
    chosen = []
    for row in rows:
        if row["type"] != "part":
            continue
        if row["fstype"] not in ACCEPTED_FS:
            continue
        if row["name"] in busy_disks or row["pkname"] in busy_disks:
            continue
        if skipped_mount(row["mounts"]):
            continue
        if not row["path"].startswith("/dev/"):
            continue
        chosen.append(row["path"])
    return chosen


def markers_ok(root):
    return (
        os.path.isfile(os.path.join(root, "etc/debian_version"))
        and os.path.isdir(os.path.join(root, "opt/jukebox"))
        and os.path.isdir(os.path.join(root, "var/lib/jukebox"))
    )


def find_installed():
    busy = live_disks()
    found = []
    for device in candidates(flatten(block_devices()), busy):
        root = "/mnt/jukebox-probe"
        os.makedirs(root, exist_ok=True)
        mounted = subprocess.run(["mount", "-o", "ro", device, root], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        try:
            if mounted and markers_ok(root):
                found.append(device)
        finally:
            if mounted:
                subprocess.run(["umount", root], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return found


def bind(src, dst):
    os.makedirs(dst, exist_ok=True)
    subprocess.check_call(["mount", "--bind", src, dst])


def install_on(device):
    root = "/mnt/jukebox-target"
    os.makedirs(root, exist_ok=True)
    subprocess.check_call(["mount", device, root])
    if not markers_ok(root):
        subprocess.run(["umount", root], check=False)
        return 1
    binds = []
    try:
        for src, name in (("/dev", "dev"), ("/dev/pts", "dev/pts"), ("/proc", "proc"), ("/sys", "sys")):
            dst = os.path.join(root, name)
            bind(src, dst)
            binds.append(dst)
        timeout = os.path.join(root, "etc/apt/apt.conf.d")
        os.makedirs(timeout, exist_ok=True)
        with open(os.path.join(timeout, "99jukebox-usb-timeout"), "w", encoding="utf-8") as handle:
            handle.write('Acquire::http::Timeout "8";\nAcquire::https::Timeout "8";\n')
        os.makedirs(os.path.join(root, "tmp"), exist_ok=True)
        shutil.copy(DEB, os.path.join(root, "tmp/videojukebox.deb"))
        env = os.environ.copy()
        env["DEBIAN_FRONTEND"] = "noninteractive"
        env["LANG"] = "C.UTF-8"
        result = subprocess.run(["chroot", root, "dpkg", "-i", "/tmp/videojukebox.deb"], env=env)
        subprocess.run(["sync"], check=False)
        return result.returncode
    finally:
        for dst in reversed(binds):
            subprocess.run(["umount", dst], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(["umount", root], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def self_test():
    sample = [
        {"name": "sda", "path": "/dev/sda", "type": "disk", "fstype": "", "pkname": "", "mounts": []},
        {"name": "sda1", "path": "/dev/sda1", "type": "part", "fstype": "vfat", "pkname": "sda", "mounts": ["/boot/efi"]},
        {"name": "sda2", "path": "/dev/sda2", "type": "part", "fstype": "ext4", "pkname": "sda", "mounts": []},
        {"name": "sdb", "path": "/dev/sdb", "type": "disk", "fstype": "iso9660", "pkname": "", "mounts": ["/run/live/medium"]},
        {"name": "vda2", "path": "/dev/vda2", "type": "part", "fstype": "ext4", "pkname": "vda", "mounts": []},
    ]
    chosen = candidates(sample, {"sdb", "vda", "vdb"})
    if chosen != ["/dev/sda2"]:
        raise SystemExit(f"expected the installed partition, got {chosen}")
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        if markers_ok(tmp):
            raise SystemExit("an empty directory must be ignored")
        os.makedirs(os.path.join(tmp, "etc"))
        os.makedirs(os.path.join(tmp, "opt/jukebox"))
        os.makedirs(os.path.join(tmp, "var/lib/jukebox"))
        with open(os.path.join(tmp, "etc/debian_version"), "w", encoding="utf-8") as handle:
            handle.write("13\n")
        if not markers_ok(tmp):
            raise SystemExit("the installed system was not recognized")
    print("ok")
    return 0


def main(argv):
    if len(argv) > 1 and argv[1] == "self-test":
        return self_test()
    if not os.path.isfile(DEB):
        say("Opdateringsfilen mangler på USB-nøglen.")
        say("Der er ikke ændret noget. Sluk, tag nøglen ud, og tænd igen.")
        time.sleep(3600)
        return 1
    say("Opdaterer Videojukebox.")
    say("Vent. Sluk ikke computeren.")
    found = []
    for _ in range(15):
        found = find_installed()
        if found:
            break
        time.sleep(2)
    if len(found) != 1:
        say("Jeg kan ikke finde den installerede Videojukebox.")
        say("Der er ikke slettet noget. Sluk, tag USB-nøglen ud, og tænd igen.")
        time.sleep(3600)
        return 1
    code = install_on(found[0])
    if code != 0:
        say("Opdateringen blev ikke gennemført.")
        say("Der er ikke slettet noget. Sluk, tag USB-nøglen ud, og tænd igen.")
        time.sleep(3600)
        return code
    say("Færdig. Tag USB-nøglen ud nu.")
    say("Computeren starter igen om et øjeblik.")
    time.sleep(12)
    subprocess.run(["reboot"], check=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
