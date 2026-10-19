using Jukebox.Core;
using Jukebox.Core.Backup;
using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Jukebox.Core.Indexing;
using Jukebox.Core.Metadata;
using Jukebox.Core.Monitoring;
using Jukebox.Core.Playback;
using Jukebox.Core.Seeding;
using Jukebox.Core.Thumbnails;
using Jukebox.Host;

var startedUtc = DateTime.UtcNow;
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();
var config = builder.Configuration.GetSection("Jukebox").Get<JukeboxConfig>() ?? new JukeboxConfig();
var envData = Environment.GetEnvironmentVariable("JUKEBOX_DATA_DIR");
if (!string.IsNullOrWhiteSpace(envData))
    config.DataDirectory = envData;

var paths = new JukeboxPaths
{
    DataDirectory = JukeboxPaths.ResolveDataDirectory(config.DataDirectory, builder.Environment.ContentRootPath)
};
paths.Ensure();

if (args.Contains("--self-check"))
{
    var checkDb = new JukeboxDatabase(paths);
    checkDb.Migrate();
    var integrity = checkDb.IntegrityCheck();
    Console.WriteLine(integrity == "ok" ? $"ok {JukeboxVersion.Current}" : integrity);
    return integrity == "ok" ? 0 : 1;
}

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<JukeboxDatabase>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddSingleton<QueueStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<BackupStore>();
builder.Services.AddSingleton<FfprobeRunner>();
builder.Services.AddSingleton<ThumbnailGenerator>();
builder.Services.AddSingleton<MediaIndexer>();
builder.Services.AddSingleton<UsbSongCopy>();
builder.Services.AddSingleton<PlaybackCoordinator>();
builder.Services.AddSingleton<BackupManager>();
builder.Services.AddSingleton<SystemSampler>();
builder.Services.AddSingleton<OnlimeShareService>();
if (config.EnableBackground)
{
    builder.Services.AddHostedService<PidFile>();
    builder.Services.AddHostedService<IndexerWorker>();
    builder.Services.AddHostedService<PlaybackWorker>();
    builder.Services.AddHostedService<BackupWorker>();
    builder.Services.AddHostedService<SamplerWorker>();
    builder.Services.AddHostedService<OnlimeShareWorker>();
}

var app = builder.Build();
var database = app.Services.GetRequiredService<JukeboxDatabase>();
database.Migrate();
var settings = app.Services.GetRequiredService<SettingsStore>();
settings.EnsureDefaults(new ClubSettings
{
    PublicBaseUrl = config.PublicBaseUrl,
    IdleTimeoutSeconds = config.IdleTimeoutSeconds
}, config.DefaultPin);

var media = app.Services.GetRequiredService<MediaStore>();
media.FoldDecadeTags();
media.FoldDanishTags();
var sources = new List<MediaSource>
{
    new() { Id = "onlime-media", Name = "Onlime sync", Kind = "onlime", Path = paths.OnlimeMedia, Priority = 1, Enabled = true, Role = LibraryRoles.Catalog },
    new() { Id = "smb-media", Name = "Network share", Kind = "smb", Path = paths.Smb, Priority = 2, Enabled = true, Role = LibraryRoles.Catalog },
    new() { Id = "local-media", Name = "Local library", Kind = "local", Path = paths.Library, Priority = 3, Enabled = true, Role = LibraryRoles.Catalog },
    new() { Id = "onlime-events", Name = "Club screens", Kind = "onlime", Path = paths.OnlimeEvents, Priority = 1, Enabled = true, Role = LibraryRoles.Event },
    new() { Id = "onlime-reklamer", Name = "Reklamer", Kind = "onlime", Path = paths.OnlimeReklamer, Priority = 1, Enabled = true, Role = LibraryRoles.Event }
};
if (config.WatchUsb)
{
    sources.Add(new MediaSource { Id = "usb-media", Name = "USB drives", Kind = "usb", Path = "/media", Priority = 4, Enabled = true, Role = LibraryRoles.Catalog });
    sources.Add(new MediaSource { Id = "usb-run", Name = "USB user mounts", Kind = "usb", Path = "/run/media", Priority = 5, Enabled = true, Role = LibraryRoles.Catalog });
}

media.SeedSources(sources);
media.UpsertSource(sources.First(source => source.Id == "onlime-reklamer"));
var queue = app.Services.GetRequiredService<QueueStore>();
if (PowerGap.ForgetQueue(PowerGap.Read(paths.LastSeen), DateTime.UtcNow))
    queue.ClearAll();
else
    queue.ResetInterrupted();
PowerGap.Touch(paths.LastSeen);
WelcomePoster.Write(paths.WelcomeCard, settings.Get().ClubName, paths.LogoPath);
HallPlayback.ClearStalePlay(paths.PlayRequest, paths.PlayDone, paths.PlayClock);
if (config.SeedSampleMedia)
    await SampleLibrarySeeder.SeedAsync(paths, CancellationToken.None);

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
Endpoints.Map(app, startedUtc);
app.MapFallback(async context =>
{
    var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
    if (context.Request.Path.StartsWithSegments("/api") || !File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Not found");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});

app.Run();
return 0;

public partial class Program;
