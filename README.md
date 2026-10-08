# Club video jukebox

A local, offline video jukebox for a Linux touchscreen PC in a golf club, sports club, café, or community hall. Guests browse and queue films on the touchscreen or from a phone on the club Wi-Fi. The television plays the queue. Nothing in the daily path needs the internet.

The touchscreen is [the shelf](http://127.0.0.1:47123/). The television page is [the hall screen](http://127.0.0.1:47123/player). Administration is [the PIN page](http://127.0.0.1:47123/admin). The first PIN is **2580**.

## What a guest can do

- Browse by category, artist (A–Å), and search
- Add a film to a first-in, first-out queue
- See what is playing and what is waiting

A guest cannot skip, reorder, or delete the queue, and cannot open administration. A film that is playing, already queued, or played by a guest in the last 30 minutes cannot be added again. House playback does not count toward that cooldown.

## Run it here

Requirements: .NET 9 SDK, Node.js 22, ffmpeg, and ffprobe. mpv is used when two displays are connected.

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
./scripts/dev.sh
```

The script builds the React app, then starts Kestrel on port **47123**. The first launch creates short sample films under `src/Jukebox.Host/data/` so the shelf is not empty. Indexing waits until each file has stopped growing, then reads tags with ffprobe and picks a thumbnail. That takes a little while; the shelf explains itself until the films appear.

Sample media is only created when the Onlime folder and the local library are both empty. Delete those files, or turn `SeedSampleMedia` off, for a real library.

## Where films come from

Watched folders, in priority order:

| Priority | Kind | Default path |
| --- | --- | --- |
| 1 | Onlime sync | `data/onlime-sync/media` |
| 2 | SMB mount | `data/smb` |
| 3 | Local disk | `data/library` |
| 4 | USB | `/media` and `/run/media` |
| — | Event screens | `data/onlime-sync/events` |
| — | Update packages | `data/onlime-sync/updates` and `data/updates` |

Point the Onlime desktop client at `onlime-sync`. There is no Onlime API. If the sync stops, the films already on disk keep playing.

A file stays invisible until the copy is stable, ffprobe accepts it, a thumbnail exists, and the database transaction commits.

Supported files: MP4, MKV, AVI, MOV, WEBM, MP3, AAC, M4A, FLAC, WAV, OGG, JPG, PNG, WEBP.

## Two screens and audio

- Display 1, the internal touch panel, shows only the shelf.
- Display 2, HDMI or DisplayPort, shows `/player` in Chromium and full-screen mpv on top while a film plays.
- The assignment is remembered in the database and written to `data/displays.env` for the kiosk scripts.
- With one display, or no display, mpv stays off so it cannot cover the shelf. The hall page plays the film itself. This is the mode used on a development machine.
- Analog line out is preferred. Pick the device under Administration → playback. The choice survives reboot.

## Idle hall

When the queue is empty the television shows the club name, clock, date, welcome, instructions, a QR code, and posters from the events folder. It does not go black. After the idle timeout (90 seconds unless changed) the house plays something on its own: 80% least guest-played, 20% random. Those plays increment `auto_play_count` only.

## Phone on the club network

The QR code uses the public address in settings. Production should be `http://jukebox.local:47123` via Avahi. Phones get the same shelf. They do not get the PIN page from the shelf, and they cannot stream the film file. Full file playback is accepted only from the machine itself, so the sound stays on the club speakers.

## Test from a USB stick

`deploy/scripts/build-usb.sh` builds a Debian 13 live ISO with the jukebox already on it. Written to a USB stick, it boots a touchscreen PC into the shelf and plays on HDMI. It does not install to, or erase, the PC's own disk. Flashing steps, Secure Boot, and the console password are in [docs/usb.md](docs/usb.md). The image is `publish/usb/jukebox-live-amd64.iso`.

## Install on Debian

```bash
./deploy/scripts/publish.sh
sudo ./deploy/scripts/install.sh
```

This installs mpv, ffmpeg, Chromium, and Avahi, creates the `jukebox` user, and enables:

- `jukebox-update.service` — oneshot, before the app, never while it is running
- `jukebox.service` — restarts on crash, systemd watchdog, memory cap 1.2 GB
- `jukebox-kiosk.service` and `jukebox-hall.service` — enable after a graphical login exists

Backups of the SQLite database run at 03:00 local time, with 7 daily, 4 weekly, and 12 monthly copies. Restore from the admin page. On a supervised machine the process exits so systemd starts clean.

Update packages are `tar.gz` files with `manifest.json` and `SHA256SUMS`. They are installed on the next boot, checked, and rolled back if the new build fails `--self-check`.

## Layout

```
src/Jukebox.Core          domain, SQLite, indexing, metadata, playback, backup, updates
src/Jukebox.Host          Kestrel, HTTP API, background workers
src/Jukebox.UpdateAgent   boot-time updater
web                       React touchscreen, hall screen, admin
deploy                    systemd units, kiosk script, installer
docs                      architecture and operations
```

The canonical schema is `src/Jukebox.Core/Data/Migrations/001_init.sql`.

## Tests

```bash
dotnet test tests/Jukebox.Tests/Jukebox.Tests.csproj
```
