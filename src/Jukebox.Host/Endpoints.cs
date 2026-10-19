using System.Collections.Concurrent;
using System.Net;
using Jukebox.Core;
using Jukebox.Core.Backup;
using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Jukebox.Core.Indexing;
using Jukebox.Core.Metadata;
using Jukebox.Core.Monitoring;
using Jukebox.Core.Playback;
using Jukebox.Core.Qr;
using Jukebox.Core.Security;
using Jukebox.Core.Updates;

namespace Jukebox.Host;

public static class Endpoints
{
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> EnqueueWindow = new();

    public static void Map(WebApplication app, DateTime startedUtc)
    {
        InstallEndpoints.Map(app, app.Services.GetRequiredService<JukeboxConfig>());
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = JukeboxVersion.Current }));
        app.MapGet("/api/volume", () => Results.Ok(new { percent = LiveVolume.Current }));
        app.MapPost("/api/volume", (VolumeBody body, JukeboxPaths paths) =>
        {
            var report = LiveVolume.Apply(paths.DataDirectory, body.Percent);
            return Results.Ok(new { percent = report.Percent });
        });

        app.MapGet("/api/screen", (MediaStore media, SettingsStore settings, QueueStore queue, PlaybackCoordinator playback, MediaIndexer indexer) =>
        {
            var club = settings.Get();
            var (clock, dateLabel, serverTime) = Clock(club.Timezone);
            var snap = playback.Snapshot();
            var active = queue.Active();
            var counts = media.Counts();
            return Results.Ok(new
            {
                clubName = club.ClubName,
                showClubName = club.ShowClubNameOnHall,
                showQr = club.ShowQrOnHall,
                welcomeMessage = club.WelcomeMessage,
                instructions = club.Instructions,
                publicUrl = club.PublicBaseUrl,
                clock,
                dateLabel,
                serverTime,
                queue = active.Where(item => item.Status == "queued").Select(Card),
                nowPlaying = snap.Current is null ? null : Now(snap),
                events = media.Events(DateTime.UtcNow).Select(item => new
                {
                    id = item.Id,
                    title = item.Title,
                    imageUrl = Thumb(item)
                }),
                logoUrl = File.Exists(app.Services.GetRequiredService<Jukebox.Core.JukeboxPaths>().LogoPath) ? "/api/branding/logo" : null,
                engine = snap.Engine,
                phase = snap.Current is null ? "welcome" : "playing",
                libraryCount = counts.Ready,
                indexing = !indexer.InitialScanCompleted,
                version = JukeboxVersion.Current,
                cooldownMinutes = club.CooldownMinutes,
                idleTimeoutSeconds = club.IdleTimeoutSeconds,
                cooled = Cooled(media, club.CooldownMinutes, active)
            });
        });

        app.MapPost("/api/activity", (PlaybackCoordinator playback) =>
        {
            playback.Touch();
            return Results.NoContent();
        });

        app.MapPost("/api/hall/heartbeat", (PlaybackCoordinator playback) =>
        {
            playback.HallHeartbeat();
            return Results.NoContent();
        });

        app.MapPost("/api/hall/finished", (FinishedBody body, HttpContext http, PlaybackCoordinator playback) =>
        {
            var remote = http.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (string.Equals(body.QueueId, HallPlayback.WakeId, StringComparison.Ordinal))
                return Results.NoContent();
            playback.MarkFinished(body.QueueId);
            return Results.NoContent();
        });

        app.MapGet("/api/media", (string? q, string? category, string? artist, string? letter, string? type, int? limit, int? offset, MediaStore media, SettingsStore settings, QueueStore queue) =>
        {
            var items = media.Query(new CatalogFilter
            {
                Text = q,
                CategoryId = category,
                Artist = artist,
                Letter = letter,
                MediaType = type,
                Limit = limit ?? 60,
                Offset = offset ?? 0
            });
            var blocks = Blocks(media, settings, queue);
            return Results.Ok(items.Select(item => Public(item, blocks)));
        });

        app.MapGet("/api/media/{id}", (string id, MediaStore media, SettingsStore settings, QueueStore queue) =>
        {
            var item = media.Get(id);
            if (item is null || !item.IsReady || item.IsHidden || item.LibraryRole != LibraryRoles.Catalog)
                return Results.NotFound();
            var blocks = Blocks(media, settings, queue);
            return Results.Ok(Public(item, blocks));
        });

        app.MapPost("/api/queue", (EnqueueBody body, HttpContext http, QueueStore queue, PlaybackCoordinator playback) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "local";
            if (!AllowEnqueue(ip))
                return Results.Json(new { ok = false, reason = "Please wait a moment before adding more." }, statusCode: StatusCodes.Status429TooManyRequests);
            var origin = body.Origin == Origins.Mobile ? Origins.Mobile : Origins.User;
            var result = queue.TryEnqueue(body.MediaId, origin, body.ClientLabel);
            if (result.Ok)
                playback.Touch();
            return Results.Ok(new { ok = result.Ok, reason = result.Reason });
        });

        app.MapGet("/api/artists", (MediaStore media) =>
        {
            var groups = media.Artists()
                .GroupBy(item => item.Letter)
                .Select(group => new
                {
                    letter = group.Key,
                    artists = group.Select(item => new { name = item.Name, count = item.Count })
                });
            return Results.Ok(new { alphabet = ClubCulture.Alphabet, groups });
        });

        app.MapGet("/api/categories", (MediaStore media) => Results.Ok(media.Categories(publicOnly: true).Select(item => new
        {
            id = item.Id,
            name = item.Name,
            kind = item.Kind,
            count = item.Count
        })));

        app.MapGet("/api/qr.svg", (SettingsStore settings) =>
            Results.Content(QrImages.Svg(settings.Get().PublicBaseUrl), "image/svg+xml"));

        app.MapGet("/api/branding/logo", (Jukebox.Core.JukeboxPaths paths) =>
            File.Exists(paths.LogoPath) ? Results.File(paths.LogoPath, "image/png") : Results.NotFound());

        app.MapGet("/thumbnails/{id}.jpg", (string id, MediaStore media) =>
        {
            var item = media.Get(id);
            if (item?.ThumbnailPath is null || !File.Exists(item.ThumbnailPath))
                return Results.NotFound();
            return Results.File(item.ThumbnailPath, "image/jpeg");
        });

        app.MapGet("/api/content/{id}", (string id, HttpContext http, MediaStore media) =>
        {
            var remote = http.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var item = media.Get(id);
            if (item is null || !item.IsReady)
                return Results.NotFound();
            var source = media.ListSources().FirstOrDefault(entry => entry.Id == item.SourceId);
            if (source is null)
                return Results.NotFound();
            var path = SafeFile(source.Path, item.RelativePath);
            if (path is null)
                return Results.NotFound();
            var type = item.MediaType switch
            {
                MediaTypes.Video => "video/mp4",
                MediaTypes.Audio => "audio/mpeg",
                _ => "image/jpeg"
            };
            if (item.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                type = "image/png";
            if (item.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                type = "image/webp";
            if (item.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                type = "audio/wav";
            if (item.Extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || item.Extension.Equals(".opus", StringComparison.OrdinalIgnoreCase))
                type = "audio/ogg";
            if (item.Extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
                type = "audio/flac";
            if (item.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) || item.Extension.Equals(".aac", StringComparison.OrdinalIgnoreCase))
                type = "audio/mp4";
            return Results.File(path, type, enableRangeProcessing: true);
        });

        var admin = app.MapGroup("/api/admin");
        admin.MapPost("/login", (LoginBody body, HttpContext http, SettingsStore settings, SessionStore sessions) =>
        {
            if (!PinHasher.Verify(body.Pin ?? "", settings.GetPinHash()))
                return Results.Json(new { ok = false }, statusCode: StatusCodes.Status401Unauthorized);
            var token = sessions.Create(TimeSpan.FromHours(8));
            http.Response.Cookies.Append("jukebox_admin", token, CookieOptions());
            settings.Audit("login", "Administrator signed in");
            return Results.Ok(new { ok = true });
        });

        admin.MapGet("/session", (HttpContext http, SessionStore sessions) =>
            Results.Ok(new { authenticated = sessions.IsValid(http.Request.Cookies["jukebox_admin"]) }));

        admin.MapPost("/logout", (HttpContext http, SessionStore sessions) =>
        {
            sessions.Revoke(http.Request.Cookies["jukebox_admin"]);
            http.Response.Cookies.Delete("jukebox_admin");
            return Results.NoContent();
        });

        admin.MapGet("/overview", (MediaStore media, SettingsStore settings, SystemSampler sampler, PlaybackCoordinator playback) =>
        {
            var counts = media.Counts();
            var club = settings.Get();
            var paths = app.Services.GetRequiredService<Jukebox.Core.JukeboxPaths>();
            return Results.Ok(sampler.Build(paths.DatabasePath, counts.Ready, counts.Hidden, counts.Errors, club.LastScanAt, club.LastBackupAt, startedUtc, playback.Snapshot().LastError));
        }).AddEndpointFilter(Gate);

        admin.MapGet("/media", (string? q, int? limit, int? offset, MediaStore media) =>
        {
            var items = media.Query(new CatalogFilter
            {
                Text = q,
                Limit = limit ?? 80,
                Offset = offset ?? 0,
                IncludeUnready = true
            });
            return Results.Ok(items.Select(item => new
            {
                id = item.Id,
                title = item.Title,
                artist = item.Artist,
                album = item.Album,
                genre = item.Genre,
                year = item.Year,
                mediaType = item.MediaType,
                libraryRole = item.LibraryRole,
                durationMs = item.DurationMs,
                fileSize = item.FileSize,
                width = item.Width,
                height = item.Height,
                bitrate = item.Bitrate,
                videoCodec = item.VideoCodec,
                audioCodec = item.AudioCodec,
                isHidden = item.IsHidden,
                isReady = item.IsReady,
                indexState = item.IndexState,
                error = item.ErrorMessage,
                hasOverride = item.HasOverride,
                userPlayCount = item.UserPlayCount,
                autoPlayCount = item.AutoPlayCount,
                thumbnailUrl = Thumb(item),
                relativePath = item.RelativePath,
                sourceId = item.SourceId,
                availableFrom = item.AvailableFrom,
                availableUntil = item.AvailableUntil,
                categoryIds = media.CategoryIds(item.Id)
            }));
        }).AddEndpointFilter(Gate);

        admin.MapPut("/media/{id}", (string id, MediaEdit body, MediaStore media, SettingsStore settings) =>
        {
            var item = media.Get(id);
            if (item is null)
                return Results.NotFound();
            media.SaveOverride(id, new MetadataOverride
            {
                Title = body.Title,
                Artist = body.Artist,
                Album = body.Album,
                Genre = body.Genre,
                Year = body.Year
            });
            var updated = media.Get(id)!;
            var resolved = MetadataMerger.ResolveStored(updated, media.GetOverride(id));
            media.ApplyResolved(id, resolved, Categorizer.Build(updated.MediaType, resolved.Genre, resolved.Year, resolved.Title, resolved.Artist, resolved.Album), true);
            if (body.Hidden is bool hidden)
                media.SetHidden(id, hidden);
            media.SetWindow(id, body.AvailableFrom, body.AvailableUntil);
            if (body.CategoryIds is not null)
                media.SetManualCategories(id, body.CategoryIds);
            settings.Audit("metadata", id);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapDelete("/media/{id}", (string id, MediaStore media, QueueStore queue, SettingsStore settings, JukeboxPaths paths) =>
        {
            var item = media.Get(id);
            if (item is null)
                return Results.NotFound();
            var full = LocalCopy.InsideLibrary(paths.Library, item.RelativePath);
            if (item.SourceId != LocalCopy.SourceId || full is null)
                return Results.Json(new { ok = false, reason = "Only a song copied from USB can be deleted." }, statusCode: StatusCodes.Status409Conflict);
            if (queue.IsPlaying(id))
                return Results.Json(new { ok = false, reason = "The song is playing. Wait until it finishes." }, statusCode: StatusCodes.Status409Conflict);
            try
            {
                if (File.Exists(full))
                    File.Delete(full);
            }
            catch (Exception)
            {
                return Results.Json(new { ok = false, reason = "The song file could not be removed." }, statusCode: StatusCodes.Status409Conflict);
            }

            if (!string.IsNullOrWhiteSpace(item.ThumbnailPath))
            {
                try
                {
                    if (File.Exists(item.ThumbnailPath))
                        File.Delete(item.ThumbnailPath);
                }
                catch (Exception)
                {
                    // The song itself is already gone.
                }
            }

            media.DeleteItem(id);
            settings.Audit("delete-song", item.RelativePath);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapDelete("/queue", (PlaybackCoordinator playback, SettingsStore settings, JukeboxPaths paths) =>
        {
            playback.ClearQueue();
            try
            {
                HallPlayback.AtomicWrite(paths.PlayRequest, HallPlayback.StopRequest());
            }
            catch (IOException)
            {
                // The player notices the empty queue on its next look.
            }

            settings.Audit("queue-clear", null);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapDelete("/media/{id}/override", (string id, MediaStore media, SettingsStore settings) =>
        {
            var item = media.Get(id);
            if (item is null)
                return Results.NotFound();
            media.ClearOverride(id);
            var cleared = media.Get(id)!;
            var resolved = MetadataMerger.ResolveStored(cleared, null);
            media.ApplyResolved(id, resolved, Categorizer.Build(cleared.MediaType, resolved.Genre, resolved.Year, resolved.Title, resolved.Artist, cleared.Album), false);
            settings.Audit("metadata-reset", id);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapPost("/library/reindex", (MediaStore media, SettingsStore settings) =>
        {
            media.MarkForReindex();
            settings.Audit("reindex", null);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapPost("/library/rebuild", (MediaStore media, SettingsStore settings) =>
        {
            media.DeleteLibrary();
            settings.Audit("rebuild", null);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapGet("/categories", (MediaStore media) => Results.Ok(media.Categories(false))).AddEndpointFilter(Gate);
        admin.MapPost("/categories", (NameBody body, MediaStore media, SettingsStore settings) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest();
            var id = media.AddCustomCategory(body.Name);
            settings.Audit("category", body.Name);
            return Results.Ok(new { id });
        }).AddEndpointFilter(Gate);
        admin.MapPut("/categories/{id}", (string id, CategoryEdit body, MediaStore media) =>
        {
            media.UpdateCategory(id, body.Name, body.Hidden);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);
        admin.MapPut("/genres/{id}/hidden", (string id, GenreHidden body, MediaStore media) =>
        {
            if (!media.SetGenreHidden(id, body.Hidden))
                return Results.NotFound();
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);
        admin.MapDelete("/categories/{id}", (string id, MediaStore media) =>
        {
            media.DeleteCategory(id);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapGet("/sources", (MediaStore media) => Results.Ok(media.ListSources())).AddEndpointFilter(Gate);
        admin.MapPost("/sources", (SourceBody body, MediaStore media, SettingsStore settings) =>
        {
            if (!ValidPath(body.Path))
                return Results.BadRequest(new { ok = false, reason = "Use a full folder path, not the disk root." });
            var source = new MediaSource
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = body.Name.Trim(),
                Kind = body.Kind,
                Path = Path.GetFullPath(body.Path),
                Priority = body.Priority,
                Enabled = body.Enabled,
                Role = body.Role == LibraryRoles.Event ? LibraryRoles.Event : LibraryRoles.Catalog
            };
            media.UpsertSource(source);
            settings.Audit("source-add", source.Path);
            return Results.Ok(source);
        }).AddEndpointFilter(Gate);
        admin.MapPut("/sources/{id}", (string id, SourceBody body, MediaStore media, SettingsStore settings) =>
        {
            if (!ValidPath(body.Path))
                return Results.BadRequest(new { ok = false, reason = "Use a full folder path, not the disk root." });
            media.UpsertSource(new MediaSource
            {
                Id = id,
                Name = body.Name.Trim(),
                Kind = body.Kind,
                Path = Path.GetFullPath(body.Path),
                Priority = body.Priority,
                Enabled = body.Enabled,
                Role = body.Role == LibraryRoles.Event ? LibraryRoles.Event : LibraryRoles.Catalog
            });
            settings.Audit("source", id);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);
        admin.MapDelete("/sources/{id}", (string id, MediaStore media, SettingsStore settings) =>
        {
            media.DeleteSource(id);
            settings.Audit("source-delete", id);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapGet("/settings", (SettingsStore settings) => Results.Ok(settings.Get())).AddEndpointFilter(Gate);
        admin.MapPut("/settings", (ClubSettings body, SettingsStore settings, JukeboxPaths paths) =>
        {
            settings.Save(body);
            try
            {
                HallPlayback.AtomicWrite(paths.AudioEnv, HallPlayback.RouteFile(body.AudioDevice, body.VolumePercent));
            }
            catch (IOException)
            {
                // The player reads the saved value on its next pass.
            }

            settings.Audit("settings", body.IdleStrategy);
            return Results.Ok(settings.Get());
        }).AddEndpointFilter(Gate);
        admin.MapGet("/volume-status", (JukeboxPaths paths) =>
        {
            var heard = LiveVolume.Read(paths.DataDirectory);
            return Results.Ok(new
            {
                percent = heard?.Percent,
                socket = heard?.Socket ?? false,
                pipe = heard?.Pipe ?? false,
                alsa = heard?.Alsa ?? false
            });
        }).AddEndpointFilter(Gate);
        admin.MapPost("/pin", (PinBody body, SettingsStore settings) =>
        {
            if (!PinHasher.Verify(body.Current ?? "", settings.GetPinHash()))
                return Results.Json(new { ok = false, reason = "The current PIN is wrong." }, statusCode: StatusCodes.Status401Unauthorized);
            if (body.Next is null || body.Next.Length is < 4 or > 8 || body.Next.Any(ch => !char.IsDigit(ch)))
                return Results.BadRequest(new { ok = false, reason = "Use 4 to 8 digits." });
            settings.SetPin(PinHasher.Hash(body.Next));
            settings.Audit("pin", "PIN changed");
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapGet("/audio-devices", () => Results.Ok(DisplayAudioProbe.QueryAudio())).AddEndpointFilter(Gate);
        admin.MapGet("/displays", (SettingsStore settings) =>
        {
            var displays = DisplayAudioProbe.QueryDisplays();
            var club = settings.Get();
            return Results.Ok(new { displays, ui = club.UiOutput, playback = club.PlaybackOutput });
        }).AddEndpointFilter(Gate);

        admin.MapGet("/backups", (BackupStore store) => Results.Ok(store.List())).AddEndpointFilter(Gate);
        admin.MapPost("/backups", (BackupManager manager, SettingsStore settings) =>
        {
            var record = manager.Create("manual", "Created by the administrator");
            settings.Audit("backup", record.Id);
            return Results.Ok(record);
        }).AddEndpointFilter(Gate);
        admin.MapPost("/backups/{id}/restore", (string id, BackupManager manager, IHostApplicationLifetime lifetime, SettingsStore settings) =>
        {
            manager.Restore(id);
            settings.Audit("restore", id);
            if (Environment.GetEnvironmentVariable("JUKEBOX_SUPERVISED") == "1")
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(400);
                    lifetime.StopApplication();
                });
            }
            return Results.Ok(new { ok = true, restarted = Environment.GetEnvironmentVariable("JUKEBOX_SUPERVISED") == "1" });
        }).AddEndpointFilter(Gate);
        admin.MapDelete("/backups/{id}", (string id, BackupManager manager) =>
        {
            manager.Delete(id);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(Gate);

        admin.MapGet("/usb-songs", (UsbSongCopy songs) => Results.Ok(songs.Status())).AddEndpointFilter(Gate);
        admin.MapPost("/usb-songs", (UsbSongCopy songs, SettingsStore settings) =>
        {
            var result = songs.Start();
            if (result.Started)
                settings.Audit("usb-songs", null);
            return result.Ok
                ? Results.Ok(result.Body)
                : Results.Json(result.Body, statusCode: result.Status);
        }).AddEndpointFilter(Gate);
        admin.MapGet("/package", () => Results.Ok(UsbPackage.Status())).AddEndpointFilter(Gate);
        admin.MapPost("/package", (SettingsStore settings) =>
        {
            var result = UsbPackage.StartInstall();
            if (result.Ok)
                settings.Audit("usb-install", null);
            return result.Ok
                ? Results.Ok(new { ok = true, message = result.Message })
                : Results.Json(new { ok = false, reason = result.Message }, statusCode: result.Status);
        }).AddEndpointFilter(Gate);

        admin.MapGet("/updates", (JukeboxConfig config, Jukebox.Core.JukeboxPaths paths) =>
        {
            var roots = new List<string> { paths.OnlimeUpdates, paths.LocalUpdates, Path.Combine(paths.Smb, "updates") };
            if (config.WatchUsb)
            {
                roots.Add("/media");
                roots.Add("/run/media");
            }
            return Results.Ok(new
            {
                version = JukeboxVersion.Current,
                packages = UpdateCatalog.Scan(roots, JukeboxVersion.Current),
                note = "En pakke i Onlime-mappen updates lægges på ved næste genstart. USB-nøglen installeres med knappen ovenfor."
            });
        }).AddEndpointFilter(Gate);

        admin.MapGet("/audit", (SettingsStore settings) => Results.Ok(settings.RecentAudit(40))).AddEndpointFilter(Gate);
        admin.MapGet("/wifi/current", () => Results.Ok(Wifi(WirelessProbe.Current()))).AddEndpointFilter(Gate);
        admin.MapGet("/wifi", InstallEndpoints.ListWifi).AddEndpointFilter(Gate);
        admin.MapPost("/wifi", async (WifiBody body) => await InstallEndpoints.ConnectWifi(body.Ssid, body.Psk)).AddEndpointFilter(Gate);
        admin.MapGet("/onlime/status", () => Results.Ok(OnlimeSync.Status())).AddEndpointFilter(Gate);
        admin.MapPost("/onlime/install", (SettingsStore settings) =>
        {
            var result = OnlimeSync.Install();
            if (result.Ok)
                settings.Audit("onlime-install", null);
            return result.Ok
                ? Results.Ok(new { ok = true, message = result.Message })
                : Results.Json(new { ok = false, reason = result.Message }, statusCode: result.Status);
        }).AddEndpointFilter(Gate);
        admin.MapPost("/onlime/open", () => InstallEndpoints.OpenOnlimeBrowser()).AddEndpointFilter(Gate);
        admin.MapPost("/onlime", async (OnlimeBody body) => await InstallEndpoints.Begin(body.Token, body.Device, startSync: true)).AddEndpointFilter(Gate);
    }

    private static async ValueTask<object?> Gate(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sessions = http.RequestServices.GetRequiredService<SessionStore>();
        if (!sessions.IsValid(http.Request.Cookies["jukebox_admin"]))
            return Results.Json(new { ok = false, reason = "Sign in with the club PIN." }, statusCode: StatusCodes.Status401Unauthorized);
        return await next(context);
    }

    private static Dictionary<string, string> Blocks(MediaStore media, SettingsStore settings, QueueStore queue)
    {
        var map = new Dictionary<string, string>();
        foreach (var item in queue.Active())
            map[item.MediaId] = item.Status == "playing"
                ? "This is playing now."
                : "This is already in the queue.";
        var cooldown = settings.Get().CooldownMinutes;
        if (cooldown > 0)
        {
            foreach (var id in media.RecentHumanPlays(DateTime.UtcNow.AddMinutes(-cooldown)))
                map.TryAdd(id, "This was played recently. You can choose it again later.");
        }

        return map;
    }

    private static object Public(MediaItem item, Dictionary<string, string> blocks)
    {
        blocks.TryGetValue(item.Id, out var reason);
        return new
        {
            id = item.Id,
            title = item.Title,
            artist = item.Artist,
            album = item.Album,
            genre = item.Genre,
            year = item.Year,
            decade = item.Decade,
            mediaType = item.MediaType,
            durationMs = item.DurationMs,
            thumbnailUrl = Thumb(item),
            canQueue = reason is null,
            blockReason = reason
        };
    }

    private static string[] Cooled(MediaStore media, int minutes, IReadOnlyList<QueueEntry> active)
    {
        if (minutes <= 0)
            return [];
        var busy = new HashSet<string>(active.Select(item => item.MediaId));
        return media.RecentHumanPlays(DateTime.UtcNow.AddMinutes(-minutes)).Where(id => !busy.Contains(id)).ToArray();
    }

    private static object? Wifi(Jukebox.Core.Network.WirelessLink? link) =>
        link is null ? null : new { ssid = link.Ssid, signal = link.Signal };

    private static object Card(QueueEntry item) => new
    {
        id = item.Id,
        mediaId = item.MediaId,
        title = item.Title,
        artist = item.Artist,
        mediaType = item.MediaType,
        thumbnailUrl = item.ThumbnailPath is null ? null : $"/thumbnails/{item.MediaId}.jpg",
        origin = item.Origin
    };

    private static object Now(PlaybackSnapshot snap)
    {
        var entry = snap.Current!.Entry;
        return new
        {
            queueId = entry.Id,
            mediaId = entry.MediaId,
            title = entry.Title,
            artist = entry.Artist,
            album = entry.Album,
            mediaType = entry.MediaType,
            thumbnailUrl = entry.ThumbnailPath is null ? null : $"/thumbnails/{entry.MediaId}.jpg",
            contentUrl = $"/api/content/{entry.MediaId}",
            positionMs = snap.PositionMs,
            durationMs = snap.Current.DurationMs,
            origin = entry.Origin,
            clientLabel = entry.ClientLabel
        };
    }

    private static string? Thumb(MediaItem item) =>
        item.ThumbnailPath is null ? null : $"/thumbnails/{item.Id}.jpg";

    private static (string Clock, string DateLabel, string ServerTime) Clock(string zoneId)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        return (
            local.ToString("HH:mm"),
            local.ToString("dddd d. MMMM", ClubCulture.Danish),
            new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToString("o"));
    }

    private static bool AllowEnqueue(string ip)
    {
        var window = EnqueueWindow.GetOrAdd(ip, _ => new Queue<DateTime>());
        lock (window)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (window.Count > 0 && window.Peek() < cutoff)
                window.Dequeue();
            if (window.Count >= 20)
                return false;
            window.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    private static CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = TimeSpan.FromHours(8),
        Path = "/"
    };

    private static bool ValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return false;
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return !string.IsNullOrEmpty(root) && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
    }

    private static string? SafeFile(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        return File.Exists(full) ? full : null;
    }

    private sealed record VolumeBody(int Percent);
    private sealed record EnqueueBody(string MediaId, string? Origin, string? ClientLabel);
    private sealed record FinishedBody(string? QueueId);
    private sealed record LoginBody(string? Pin);
    private sealed record MediaEdit(string? Title, string? Artist, string? Album, string? Genre, int? Year, bool? Hidden, string? AvailableFrom, string? AvailableUntil, List<string>? CategoryIds);
    private sealed record NameBody(string? Name);
    private sealed record CategoryEdit(string Name, bool Hidden);
    private sealed record GenreHidden(bool Hidden);
    private sealed record SourceBody(string Name, string Kind, string Path, int Priority, bool Enabled, string Role);
    private sealed record PinBody(string? Current, string? Next);
}
