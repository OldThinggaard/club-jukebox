#!/bin/bash
# The firmware starts one file. That file has to be Debian's shim, which
# Microsoft has signed. GRUB's own program is signed by Debian only, and the
# firmware rejects it while Secure Boot is on. Copy the shim into place and
# point the firmware list at it. Do not call GRUB's installer: with Secure
# Boot switched off it writes its own program over the shim.
set -u
BUNDLE="${JUKEBOX_SECURE_BOOT_DIR:-/usr/share/jukebox/secure-boot}"
cmd=repair
if [[ "${1:-}" == "self-test" ]]; then
  cmd=self-test
  BUNDLE="${2:-$BUNDLE}"
fi
exec python3 - "$cmd" "$BUNDLE" << 'PY'
import os
import re
import struct
import subprocess
import sys
import uuid
from pathlib import Path

EFI_GLOBAL = "8be4df61-93ca-11d2-aa0d-00e098032b8c"
LOG = Path("/var/log/jukebox-secure-boot.log")
ATTR = 7
LOAD_ACTIVE = 1
FS_IOC_GETFLAGS = 0x80086601
FS_IOC_SETFLAGS = 0x40086602
FS_IMMUTABLE_FL = 0x00000010
EFI_PART = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b"


def say(text):
    print(text, file=sys.stderr)
    try:
        with LOG.open("a", encoding="utf-8") as handle:
            handle.write(text + "\n")
    except OSError:
        pass


def microsoft_signed(path):
    data = Path(path).read_bytes()
    return b"Microsoft Corporation UEFI CA" in data


def guid_bytes(text):
    return uuid.UUID(text).bytes_le


def hd_node(part, start_lba, size_lba, part_guid):
    return struct.pack(
        "<BBHIQQ16sBB",
        0x04,
        0x01,
        42,
        part,
        start_lba,
        size_lba,
        guid_bytes(part_guid),
        0x02,
        0x02,
    )


def file_node(path):
    raw = path.encode("utf-16le") + b"\x00\x00"
    return struct.pack("<BBH", 0x04, 0x04, 4 + len(raw)) + raw


def end_node():
    return struct.pack("<BBH", 0x7F, 0xFF, 4)


def load_option(part, start_lba, size_lba, part_guid, path):
    filepath = hd_node(part, start_lba, size_lba, part_guid) + file_node(path) + end_node()
    desc = "debian".encode("utf-16le") + b"\x00\x00"
    return struct.pack("<IH", LOAD_ACTIVE, len(filepath)) + desc + filepath


def var_file(directory, name):
    return Path(directory) / f"{name}-{EFI_GLOBAL}"


def walk(blob):
    part = None
    paths = []
    off = 0
    while off + 4 <= len(blob):
        kind, sub, length = struct.unpack_from("<BBH", blob, off)
        if length < 4 or off + length > len(blob):
            break
        node = blob[off : off + length]
        if kind == 0x7F:
            break
        if kind == 0x04 and sub == 0x01 and length >= 42:
            part = struct.unpack_from("<I", node, 4)[0]
        if kind == 0x04 and sub == 0x04:
            paths.append(node[4:].decode("utf-16le").split("\x00", 1)[0])
        off += length
    return part, paths


def parse_option(option):
    if len(option) < 6:
        return None, []
    _attrs, fp_len = struct.unpack_from("<IH", option, 0)
    pos = 6
    while pos + 1 < len(option):
        if option[pos : pos + 2] == b"\x00\x00":
            pos += 2
            break
        pos += 2
    else:
        return None, []
    return walk(option[pos : pos + fp_len])


def norm(path):
    text = path.replace("/", "\\")
    if not text.startswith("\\"):
        text = "\\" + text
    return text.lower()


def is_shim(path):
    name = norm(path)
    return name.endswith("\\shimx64.efi") or name.endswith("\\bootx64.efi")


def is_grub(path):
    return norm(path).endswith("\\grubx64.efi")


def clear_immutable(path):
    try:
        fd = os.open(path, os.O_RDONLY)
    except OSError:
        return
    try:
        import fcntl

        flags = struct.unpack("I", fcntl.ioctl(fd, FS_IOC_GETFLAGS, struct.pack("I", 0)))[0]
        if flags & FS_IMMUTABLE_FL:
            fcntl.ioctl(fd, FS_IOC_SETFLAGS, struct.pack("I", flags & ~FS_IMMUTABLE_FL))
    except OSError:
        pass
    finally:
        os.close(fd)
    subprocess.run(["chattr", "-i", str(path)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def read_var(path):
    data = Path(path).read_bytes()
    if len(data) < 4:
        return b""
    return data[4:]


def write_var(path, body):
    path = Path(path)
    payload = struct.pack("<I", ATTR) + body
    if path.exists():
        clear_immutable(path)
        path.unlink()
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644)
    try:
        os.write(fd, payload)
    finally:
        os.close(fd)


def boot_entries(directory):
    found = {}
    pattern = re.compile(rf"^Boot([0-9A-Fa-f]{{4}})-{re.escape(EFI_GLOBAL)}$")
    for child in Path(directory).iterdir():
        match = pattern.match(child.name)
        if not match:
            continue
        number = int(match.group(1), 16)
        try:
            part, paths = parse_option(read_var(child))
        except OSError:
            continue
        found[number] = (child, part, paths)
    return found


def read_order(directory):
    path = var_file(directory, "BootOrder")
    if not path.exists():
        return []
    body = read_var(path)
    count = len(body) // 2
    if count == 0:
        return []
    return list(struct.unpack("<" + "H" * count, body[: count * 2]))


def point_firmware(directory, part, start_lba, size_lba, part_guid):
    target = r"\EFI\debian\shimx64.efi"
    entries = boot_entries(directory)
    chosen = None
    for number, (_path, _part, paths) in entries.items():
        if any(norm(item) == norm(target) for item in paths):
            chosen = number
            break
    used = set(entries)
    if chosen is None:
        for number in range(0x100):
            if number not in used:
                chosen = number
                break
    if chosen is None:
        raise RuntimeError("no free boot number")
    write_var(
        var_file(directory, f"Boot{chosen:04X}"),
        load_option(part, start_lba, size_lba, part_guid, target),
    )
    removed = set()
    for number, (path, _part, paths) in entries.items():
        if number == chosen:
            continue
        if paths and any(is_grub(item) for item in paths):
            clear_immutable(path)
            path.unlink()
            removed.add(number)
    order = [chosen]
    for number in read_order(directory):
        if number == chosen or number in removed:
            continue
        if number in entries or number == chosen:
            order.append(number)
    for number in entries:
        if number == chosen or number in removed or number in order:
            continue
        order.append(number)
    write_var(var_file(directory, "BootOrder"), struct.pack("<" + "H" * len(order), *order))
    # BootNext is a one-shot start. On this PC it turns into start, stop, start, stop.
    check = boot_entries(directory)
    _part, paths = check[chosen][1], check[chosen][2]
    if not paths or norm(paths[-1]) != norm(target):
        raise RuntimeError("boot entry did not stick")
    if not read_order(directory) or read_order(directory)[0] != chosen:
        raise RuntimeError("boot order did not stick")
    return paths[-1]


def root_uuid():
    result = subprocess.run(["findmnt", "-n", "-o", "UUID", "/"], capture_output=True, text=True)
    found = result.stdout.strip()
    if found and found.lower() != "none":
        return found
    try:
        for line in Path("/etc/fstab").read_text(encoding="utf-8", errors="replace").splitlines():
            parts = line.split()
            if len(parts) >= 2 and parts[1] == "/" and parts[0].startswith("UUID="):
                return parts[0][5:]
    except OSError:
        pass
    return ""


def write_grub_cfg(directory, root):
    if root:
        text = (
            f"search.fs_uuid {root} root\n"
            "set prefix=($root)/boot/grub\n"
            "configfile $prefix/grub.cfg\n"
        )
    else:
        text = (
            "search --file --set=root /boot/grub/grub.cfg\n"
            "set prefix=($root)/boot/grub\n"
            "configfile $prefix/grub.cfg\n"
        )
    (directory / "grub.cfg").write_text(text, encoding="utf-8")


def place(bundle, esp, root):
    bundle = Path(bundle)
    shim = bundle / "shimx64.efi.signed"
    grub = bundle / "grubx64.efi.signed"
    if not shim.is_file() or not grub.is_file() or not microsoft_signed(shim):
        return False
    if microsoft_signed(grub):
        return False
    efi = Path(esp) / "EFI"
    efi.mkdir(parents=True, exist_ok=True)
    names = {"BOOT", "debian"}
    if efi.is_dir():
        for child in efi.iterdir():
            if not child.is_dir():
                continue
            if any((child / leaf).exists() for leaf in ("shimx64.efi", "grubx64.efi", "BOOTX64.EFI", "bootx64.efi")):
                names.add(child.name)
    for name in names:
        folder = efi / name
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "grubx64.efi").write_bytes(grub.read_bytes())
        for extra in ("mmx64.efi.signed", "fbx64.efi.signed"):
            source = bundle / extra
            if source.is_file():
                (folder / extra.replace(".signed", "")).write_bytes(source.read_bytes())
        if name == "BOOT":
            dest = folder / "BOOTX64.EFI"
        else:
            dest = folder / "shimx64.efi"
        dest.write_bytes(shim.read_bytes())
        write_grub_cfg(folder, root)
    boot = efi / "BOOT" / "BOOTX64.EFI"
    debian = efi / "debian" / "shimx64.efi"
    return boot.read_bytes() == shim.read_bytes() and debian.read_bytes() == shim.read_bytes() and microsoft_signed(boot)


def run(cmd, timeout=60):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired):
        return None


def mounted_esp():
    result = run(["findmnt", "-n", "-o", "SOURCE", "/boot/efi"])
    source = "" if result is None else result.stdout.strip()
    if source:
        resolved = run(["readlink", "-f", source])
        if resolved and resolved.stdout.strip():
            return resolved.stdout.strip()
    root = run(["findmnt", "-n", "-o", "SOURCE", "/"])
    root_dev = "" if root is None else root.stdout.strip()
    if root_dev:
        resolved = run(["readlink", "-f", root_dev])
        if resolved and resolved.stdout.strip():
            root_dev = resolved.stdout.strip()
    parent = run(["lsblk", "-no", "PKNAME", root_dev]) if root_dev else None
    root_disk = "" if parent is None else parent.stdout.strip().splitlines()[-1].strip() if parent.stdout.strip() else ""
    listing = run(["lsblk", "-Pn", "-o", "NAME,PARTTYPE,PKNAME"])
    if listing is None:
        return ""
    chosen = ""
    for line in listing.stdout.splitlines():
        fields = dict(re.findall(r'(\w+)="([^"]*)"', line))
        if fields.get("PARTTYPE", "").lower() != EFI_PART:
            continue
        if root_disk and fields.get("PKNAME") not in ("", root_disk):
            continue
        chosen = fields.get("NAME", "")
        if fields.get("PKNAME") == root_disk:
            break
    return chosen


def partition_info(device):
    name = Path(device).name
    sysfs = Path("/sys/class/block") / name
    part = int((sysfs / "partition").read_text().strip())
    start = int((sysfs / "start").read_text().strip())
    size = int((sysfs / "size").read_text().strip())
    parent = run(["lsblk", "-no", "PKNAME", device])
    disk = parent.stdout.strip().splitlines()[-1].strip() if parent and parent.stdout.strip() else name
    block = 512
    block_path = Path("/sys/class/block") / disk / "queue" / "logical_block_size"
    if block_path.exists():
        block = int(block_path.read_text().strip()) or 512
    factor = block // 512
    guid = ""
    listed = run(["lsblk", "-no", "PARTUUID", device])
    if listed and listed.stdout.strip():
        guid = listed.stdout.strip().splitlines()[-1].strip()
    if not guid:
        raise RuntimeError("ESP has no partition id")
    return part, start // factor, size // factor, guid


def ensure_esp():
    result = run(["findmnt", "-n", "-o", "TARGET", "/boot/efi"])
    if result and result.stdout.strip() == "/boot/efi":
        source = mounted_esp()
        return source
    source = mounted_esp()
    if not source:
        return ""
    Path("/boot/efi").mkdir(parents=True, exist_ok=True)
    mounted = run(["mount", source, "/boot/efi"])
    if mounted is None or mounted.returncode != 0:
        check = run(["findmnt", "-n", "-o", "TARGET", source])
        if check and check.stdout.strip():
            return source
        return ""
    return source


def ensure_efivars():
    path = Path("/sys/firmware/efi/efivars")
    if not Path("/sys/firmware/efi").is_dir():
        return None
    path.mkdir(parents=True, exist_ok=True)
    if not any(path.iterdir()):
        run(["mount", "-t", "efivarfs", "efivarfs", str(path)])
    run(["mount", "-o", "remount,rw", str(path)])
    if not any(path.iterdir()):
        return None
    return path


def install_hook():
    hook = Path("/etc/kernel/postinst.d/zzz-jukebox-secure-boot")
    try:
        hook.unlink()
    except OSError:
        pass


def repair_machine(bundle):
    if not Path("/sys/firmware/efi").is_dir():
        say("Maskinen bruger ikke UEFI. Secure Boot springes over.")
        return 0
    device = ensure_esp()
    if not device:
        say("FEJL: EFI-partitionen blev ikke fundet.")
        return 1
    if not place(bundle, "/boot/efi", root_uuid()):
        say("FEJL: Debians godkendte shim blev ikke skrevet til disken.")
        return 1
    efivars = ensure_efivars()
    if efivars is None:
        say("FEJL: PC'ens opstartsliste kan ikke ændres.")
        return 1
    try:
        part, start_lba, size_lba, part_guid = partition_info(device)
        path = point_firmware(efivars, part, start_lba, size_lba, part_guid)
    except (OSError, RuntimeError) as exc:
        say(f"FEJL: PC'ens opstartsliste peger stadig på det gamle program. {exc}")
        return 1
    install_hook()
    try:
        os.sync()
    except AttributeError:
        pass
    say(f"Disken kan starte med Secure Boot slået til. Første fil er {path}.")
    return 0


def self_test(bundle):
    bundle = Path(bundle)
    shim = bundle / "shimx64.efi.signed"
    grub = bundle / "grubx64.efi.signed"
    if not microsoft_signed(shim):
        say("FEJL: shim er ikke Microsoft-signeret.")
        return 1
    if microsoft_signed(grub):
        say("FEJL: GRUB-filen må ikke være den, firmwaren starter.")
        return 1
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        esp = Path(tmp) / "esp"
        if not place(bundle, esp, "00112233-4455-6677-8899-aabbccddeeff"):
            say("FEJL: shim blev ikke lagt som BOOTX64.EFI.")
            return 1
        cfg = (esp / "EFI" / "debian" / "grub.cfg").read_text(encoding="utf-8")
        if "00112233-4455-6677-8899-aabbccddeeff" not in cfg:
            say("FEJL: startmenuen mangler diskens id.")
            return 1
        part_guid = "00112233-4455-6677-8899-aabbccddeeff"
        sample = load_option(1, 2048, 204800, part_guid, r"\EFI\debian\shimx64.efi")
        part, paths = parse_option(sample)
        if part != 1 or paths != [r"\EFI\debian\shimx64.efi"]:
            say(f"FEJL: opstartsposten kan ikke læses tilbage: {part} {paths}")
            return 1
        if len(hd_node(1, 2048, 204800, part_guid)) != 42:
            say("FEJL: disk-posten har forkert længde.")
            return 1
        efivars = Path(tmp) / "efivars"
        efivars.mkdir()
        grub_option = load_option(1, 2048, 204800, part_guid, r"\EFI\debian\grubx64.efi")
        windows = load_option(1, 2048, 204800, part_guid, r"\EFI\Microsoft\Boot\bootmgfw.efi")
        write_var(var_file(efivars, "Boot0001"), grub_option)
        write_var(var_file(efivars, "Boot0002"), windows)
        write_var(var_file(efivars, "BootOrder"), struct.pack("<HH", 1, 2))
        first = point_firmware(efivars, 1, 2048, 204800, part_guid)
        if norm(first) != norm(r"\EFI\debian\shimx64.efi"):
            say(f"FEJL: første start er {first}")
            return 1
        names = [item.name for item in efivars.iterdir()]
        if any(name.startswith("Boot0001-") for name in names):
            say("FEJL: den gamle GRUB-post blev liggende.")
            return 1
        if not any(name.startswith("Boot0002-") for name in names):
            say("FEJL: en anden opstartspost blev slettet.")
            return 1
        again = point_firmware(efivars, 1, 2048, 204800, part_guid)
        shim_count = 0
        for _number, (_path, _part, paths) in boot_entries(efivars).items():
            if paths and is_shim(paths[-1]):
                shim_count += 1
        if shim_count != 1 or norm(again) != norm(first):
            say("FEJL: opstartslisten fik mere end én shim.")
            return 1
        if var_file(efivars, "BootNext").exists():
            say("FEJL: en engangsstart får PC'en til at starte og stoppe.")
            return 1
    return 0


def main():
    command = sys.argv[1]
    bundle = sys.argv[2]
    if command == "self-test":
        return self_test(bundle)
    if command == "repair":
        return repair_machine(bundle)
    say("FEJL: ukendt kørsel.")
    return 1


raise SystemExit(main())
PY
