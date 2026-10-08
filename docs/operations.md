# Operations

## Daily use

Guests use the touchscreen or the QR code. Staff change the welcome text, the PIN, the cooldown, the idle style, the audio output, and the display names under `/admin`.

Drop files into the Onlime media folder. They appear after the copy settles. Remove a file and it leaves the shelf on the next scan. Event posters go in the events folder and rotate on the hall screen while nothing is queued.

## Folders on a club PC

| Path | Purpose |
| --- | --- |
| `/opt/jukebox/current` | Running build |
| `/opt/jukebox/previous` | Last good build, used for rollback |
| `/opt/jukebox/updater` | Boot-time updater |
| `/var/lib/jukebox` | Database, thumbnails, backups, logs, sync folders |

Mount an SMB share at the network-share path configured in Administration, usually with an `/etc/fstab` `cifs` entry. The jukebox does not open SMB itself.

## Audio

Prefer the analog jack. In Administration → playback, choose “Analog line out” when PulseAudio or PipeWire lists it. HDMI audio is the other common device. Apply by saving; the next film uses it.

## Displays

Connect the television before signing in to the graphical session. The service remembers the output names. `deploy/scripts/kiosk.sh ui` and `kiosk.sh hall` read `displays.env` and place Chromium. mpv uses `--fs-screen` for the television index.

If only the touchscreen is connected, the dashboard still runs and the hall page can be opened by hand. Full-screen mpv waits until a second output exists.

## Backups

Automatic copies run in the configured local hour (default 03:00): daily, plus weekly on Sunday, plus monthly on the 1st. Keep counts are 7, 4, and 12. Manual copies are kept up to 10. Restore from Administration. Take a copy before a rebuild.

## Updates

Put `jukebox-<version>.tar.gz` in an updates folder. The archive must contain `manifest.json` (`{"version":"1.0.1"}`) and `SHA256SUMS` for the files. Reboot. The updater runs first, backs up, installs, runs `Jukebox.Host --self-check`, and rolls back if that fails. It will not run while `host.pid` is alive.

## PIN

The seeded PIN is 2580. Change it on the playback tab. It is stored as a PBKDF2 hash. Sessions last eight hours and are an HTTP-only cookie.

## If something stops

`systemctl status jukebox` and `journalctl -u jukebox -n 100` are the log. The service restarts itself. A film that mpv cannot open is marked abandoned and the queue moves on. Index errors stay in the admin library and never on the guest shelf.

## Resource budget

Target: Debian, 4 GB RAM, integrated graphics.

- Host under 1.2 GB (`MemoryMax`)
- One shelf Chromium and one hall Chromium, each with a small renderer limit
- One mpv, hardware decode `--hwdec=auto-safe`
- SQLite cache kept small, WAL mode, one indexer at a time

Do not add Docker, a second database, or a browser per zone.
