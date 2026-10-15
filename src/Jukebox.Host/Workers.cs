using System.Diagnostics;
using Jukebox.Core;
using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Jukebox.Core.Indexing;
using Jukebox.Core.Monitoring;
using Jukebox.Core.Playback;
using Jukebox.Core.Backup;

namespace Jukebox.Host;

public sealed class IndexerWorker(MediaIndexer indexer, ILogger<IndexerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(300, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await indexer.ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media scan failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public sealed class PlaybackWorker(
    PlaybackCoordinator coordinator,
    MediaIndexer indexer,
    SettingsStore settings,
    JukeboxConfig config,
    JukeboxPaths paths,
    ILogger<PlaybackWorker> logger) : BackgroundService
{
    private IReadOnlyList<DisplayInfo> _displays = [];
    private long _displaysAt = long.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var waitUntil = DateTime.UtcNow.AddSeconds(8);
        while (!indexer.InitialScanCompleted && DateTime.UtcNow < waitUntil && !stoppingToken.IsCancellationRequested)
            await Task.Delay(200, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            PlayJob? job = null;
            try
            {
                var club = settings.Get();
                ApplySavedAudio();
                var displays = CurrentDisplays();
                if (string.IsNullOrWhiteSpace(club.UiOutput) || string.IsNullOrWhiteSpace(club.PlaybackOutput))
                {
                    var (ui, hall) = DisplayAudioProbe.AutoAssign(displays);
                    if (!string.IsNullOrWhiteSpace(ui) && string.IsNullOrWhiteSpace(club.UiOutput))
                        settings.Set(SettingKeys.UiOutput, ui);
                    if (!string.IsNullOrWhiteSpace(hall) && string.IsNullOrWhiteSpace(club.PlaybackOutput))
                        settings.Set(SettingKeys.PlaybackOutput, hall);
                    club = settings.Get();
                }

                DisplayAudioProbe.WriteDisplayFile(paths.DisplaysFile, displays, club.UiOutput, club.PlaybackOutput);
                var screen = DisplayAudioProbe.ScreenIndex(displays, club.PlaybackOutput);
                job = coordinator.ClaimNext(screen, null, club.ImageSeconds);
                if (job is null)
                {
                    await Task.Delay(400, stoppingToken);
                    continue;
                }

                // hall-player.sh plays the file from disk in the graphical session.
                // A timer is only the fallback when that player is not there.
                // The song stays hidden until the engine is chosen. Otherwise the
                // hall browser starts it, and mpv starts the same file again.
                var playerReady = await HallPlayerReady(stoppingToken);
                coordinator.SetEngine(playerReady ? "mpv" : "timer");
                coordinator.Publish();
                var outcome = playerReady
                    ? await WaitForHallPlayer(job, stoppingToken)
                    : await WaitForShownMedia(job, stoppingToken);
                job = outcome.Job;
                coordinator.Finish(job, outcome.Result);
                job = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Playback failed");
                if (job is not null)
                    coordinator.Finish(job, PlayResult.Failed(ex.Message));
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private void ApplySavedAudio()
    {
        var fresh = settings.Get();
        try
        {
            HallPlayback.AtomicWrite(paths.AudioEnv, HallPlayback.RouteFile(fresh.AudioDevice, fresh.VolumePercent));
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not write the audio route");
        }
    }

    private IReadOnlyList<DisplayInfo> CurrentDisplays()
    {
        var now = Environment.TickCount64;
        if (_displays.Count > 0 && now - _displaysAt < 30_000)
            return _displays;
        _displaysAt = now;
        try
        {
            var found = DisplayAudioProbe.QueryDisplays();
            if (found.Count > 0)
                _displays = found;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Display probe failed");
        }

        return _displays;
    }

    private async Task<bool> HallPlayerReady(CancellationToken stoppingToken)
    {
        if (!File.Exists("/opt/jukebox/bin/hall-player.sh"))
            return false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            if (HallPlayback.StampFresh(paths.PlayerStamp, TimeSpan.FromSeconds(5)))
                return true;
            await Task.Delay(400, stoppingToken);
        }

        return HallPlayback.StampFresh(paths.PlayerStamp, TimeSpan.FromSeconds(5));
    }

    private async Task<(PlayJob Job, PlayResult Result)> WaitForHallPlayer(PlayJob job, CancellationToken stoppingToken)
    {
        ArmHall(job);
        var started = Environment.TickCount64;
        while (!stoppingToken.IsCancellationRequested)
        {
            var guest = coordinator.TakeGuestSong(job);
            if (guest is not null)
            {
                job = guest;
                ArmHall(job);
                started = Environment.TickCount64;
                continue;
            }

            var elapsed = Environment.TickCount64 - started;
            coordinator.SetPosition(Math.Min(job.DurationMs, elapsed));
            // The stored length must not end the song. The welcome screen came
            // up, and the same file started again. The player exiting is the end.
            var image = string.Equals(job.Entry.MediaType, "image", StringComparison.OrdinalIgnoreCase);
            if (HallPlayback.EndIsBelievable(elapsed) && HallPlayback.DoneIs(paths.PlayDone, job.Entry.Id))
                return (job, PlayResult.Done());
            var cap = image
                ? HallPlayback.PlaybackCap(job.DurationMs, job.Entry.MediaType)
                : TimeSpan.FromHours(3);
            if (TimeSpan.FromMilliseconds(elapsed) >= cap)
                return (job, PlayResult.Done());
            ApplySavedAudio();
            await Task.Delay(400, stoppingToken);
        }

        return (job, PlayResult.Done());
    }

    private void ArmHall(PlayJob job)
    {
        coordinator.MarkFinished(null);
        try
        {
            if (File.Exists(paths.PlayDone))
                File.Delete(paths.PlayDone);
            if (File.Exists(paths.PlayClock))
                File.Delete(paths.PlayClock);
        }
        catch (IOException)
        {
            // The player writes a new line when this title ends.
        }

        var art = job.Entry.ThumbnailPath;
        if (string.IsNullOrWhiteSpace(art) || !File.Exists(art))
            art = "";
        HallPlayback.AtomicWrite(paths.PlayRequest, HallPlayback.Request(job.Entry.Id, job.AbsolutePath, job.Entry.MediaType, job.DurationMs, art));
    }

    private async Task<(PlayJob Job, PlayResult Result)> WaitForShownMedia(PlayJob job, CancellationToken stoppingToken)
    {
        var started = Environment.TickCount64;
        while (!stoppingToken.IsCancellationRequested)
        {
            var guest = coordinator.TakeGuestSong(job);
            if (guest is not null)
            {
                job = guest;
                started = Environment.TickCount64;
                continue;
            }

            var elapsed = Environment.TickCount64 - started;
            coordinator.SetPosition(Math.Min(job.DurationMs, elapsed));
            if (coordinator.IsFinished(job.Entry.Id) || elapsed >= job.DurationMs)
                return (job, PlayResult.Done());
            await Task.Delay(200, stoppingToken);
        }

        return (job, PlayResult.Done());
    }
}

public sealed class BackupWorker(BackupManager backups, SettingsStore settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.Get().Timezone);
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
                backups.RunScheduled(local);
            }
            catch (Exception)
            {
                // The next pass retries. A failed backup must not stop the jukebox.
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public sealed class SamplerWorker(SystemSampler sampler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                sampler.Sample();
            }
            catch
            {
                // Monitoring is advisory.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public sealed class PidFile(JukeboxPaths paths) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        File.WriteAllText(paths.PidFile, Environment.ProcessId.ToString());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(paths.PidFile))
                File.Delete(paths.PidFile);
        }
        catch
        {
            // The update agent also treats a dead pid as stopped.
        }

        return Task.CompletedTask;
    }
}
