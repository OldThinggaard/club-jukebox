# Architecture

One .NET process serves the API, the built React app, indexing, the queue, and playback supervision. mpv and Chromium are separate processes. SQLite is the only database. Nothing in the daily path calls the network.

This shape fits a 4 GB machine: one runtime, a 1.2 GB memory cap on the service, one mpv, and two small Chromium windows. A second backend, a browser engine for every screen, or Docker would spend RAM the club does not have.

## Processes

| Process | Role | If it dies |
| --- | --- | --- |
| `Jukebox.Host` | API, shelf, indexer, queue, backup timer | systemd `Restart=always`. No watchdog: the host never answered it, so the song was killed and started again |
| mpv | Full-screen picture and sound on the television | The host starts it per film and starts the next film if it exits |
| Chromium shelf | Touchscreen UI only | `jukebox-kiosk.service` restarts it |
| Chromium hall | Clock, QR, posters under the film | `jukebox-hall.service` restarts it |
| `Jukebox.UpdateAgent` | Runs only before the host, at boot | A failed package leaves the previous build in place |

The host writes `data/host.pid`. The updater refuses to replace files while that process is alive.

## Modules

`Jukebox.Core` is the product. The host is a thin HTTP and worker shell. Later modules should be new classes behind the same database, not new services.

- Karaoke: a media type and a future player that can seek. The queue already records `zone_id` (`main` today).
- More rooms: another `PlaybackCoordinator` keyed by zone. The column is already on `queue_items` and `play_history`.
- Remote or multi-club admin: the PIN session and `/api/admin` are the boundary. Do not expose those routes on the guest shelf.
- Sponsor scheduling: event items already have `available_from` and `available_until`.
- Reporting: `play_history` stores guest and house plays separately.

## Displays and sound

`DisplayAudioProbe` reads `xrandr` and `pactl` (or `aplay`). Internal names (`eDP`, `LVDS`, `DSI`) become the shelf. HDMI, DP, and VGA become the television. The names are stored in settings and rewritten to `displays.env` whenever the playback worker looks.

mpv is used only when two connected outputs exist, or when playback mode is forced to `mpv`. Otherwise a timer in the host is the clock for the queue, and `/player` shows the picture. That keeps a single monitor usable and keeps headless machines from launching a full-screen window over the shelf.

Audio device strings are mpv device ids (`pulse/...` or `alsa/plughw:...`) and are passed as `--audio-device` on the next film.

## Indexing

Sources are rows, not hard-coded paths. The worker reconciles every 20 seconds:

1. Skip a source whose folder is missing. Do not delete its catalogue because of a bad path.
2. Ignore partial names (`.partial`, `.crdownload`) and hidden files.
3. Wait until the size is unchanged for 1.2 seconds.
4. Skip unchanged ready files by size and mtime, then by a sample hash if the mtime moved.
5. ffprobe, merge metadata, thumbnail, categories, then one transaction that sets `is_ready`.
6. Delete rows whose files disappeared from a source that could be listed.

Public queries require `is_ready = 1`, `is_hidden = 0`, and `library_role = catalog`.

Metadata order is administrator override, then embedded tags, then `Artist - Title` in the file name. A tag that is only the file name is ignored so `Queen - Bohemian Rhapsody.mp4` still yields Queen and Bohemian Rhapsody.

Thumbnails are JPEG files beside the database. For video, frames at 10, 20, 40, 60, and 80 percent are scored for luminance and detail. Near-black, near-white, and flat frames lose. The winner is kept.

## Search

FTS5 with the `unicode61` tokenizer and diacritics kept, so æ, ø, and å match. A typed query becomes a prefix query (`que` → `"que"*`). Artist letters use Danish culture, including Æ, Ø, and Å.

## Queue and idle

Enqueue checks are in `QueuePolicy` and applied inside a write transaction: ready, visible, catalogue, not playing, not queued, guest cooldown, length cap 100. House picks skip the guest cooldown. Guest and phone plays increment `user_play_count`. Idle picks increment `auto_play_count`.

Idle strategy `hybrid` takes the least guest-played candidate 80 percent of the time and a random candidate from the shortlist otherwise.

## Backup and update

Backups use the SQLite online backup API. Retention is applied in `RetentionPolicy`. Restore replaces the database file after an integrity check. Under systemd (`JUKEBOX_SUPERVISED=1`) the host then stops so the service restarts clean.

Updates are extracted to `staging`, checksum-verified, self-checked, swapped with `current`, and self-checked again. Failure moves `previous` back. The updater binary lives in `/opt/jukebox/updater`, outside the tree it replaces.

## HTTP boundary

Guest: `/api/media`, `/api/queue`, `/api/screen`, `/api/artists`, `/api/categories`, `/api/activity`, thumbnails, QR.

Loopback only: `/api/content/{id}` so a phone cannot pull the film.

Admin: `/api/admin/*` with an HTTP-only cookie. The PIN is PBKDF2-SHA256. The shelf has no link to `/admin`.
