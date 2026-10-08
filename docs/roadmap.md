# Roadmap

The base jukebox in this repository covers the guest shelf, hall screen, queue, indexing, metadata, thumbnails, administration, monitoring, backup, and boot-time update. The phases below are the order that work was built, and what is deliberately left for a later module.

| Phase | Result in this tree |
| --- | --- |
| 1. Architecture | `docs/architecture.md` and the project split |
| 2. Database | `src/Jukebox.Core/Data/Migrations/001_init.sql` |
| 3. API | `src/Jukebox.Host/Endpoints.cs` |
| 4. Discovery | `Indexing/MediaIndexer.cs` |
| 5. Metadata | ffprobe, filename parser, merger, categories |
| 6. Queue | FIFO policy, cooldown, guest vs house counts |
| 7. mpv | `MpvPlayer`, display and audio probe |
| 8. Touchscreen | `web/src/Kiosk.tsx` and the Danish keyboard |
| 9. Administration | `/admin` PIN, metadata, sources, playback |
| 10. Phones | Same shelf, QR, loopback-only media files |
| 11. Monitoring | `/api/admin/overview` |
| 12. Backup | SQLite backup, retention, restore |
| 13. Updates | `Jukebox.UpdateAgent` and the systemd oneshot |
| 14. Install | `deploy/scripts/install.sh` and the unit files |

## Not in this version

- Karaoke scoring and pitch control
- A second simultaneous playback zone
- Administration from outside the building
- One database for many clubs
- A calendar for sponsor campaigns beyond the event window columns
- A printable statistics report

Those hang off `zone_id`, `available_from` / `available_until`, `play_history`, and the `/api/admin` boundary. They should not require a second process.
