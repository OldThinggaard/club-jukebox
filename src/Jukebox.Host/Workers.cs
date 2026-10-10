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
    private readonly TimerPlayer _timer = new();

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
                var displays = DisplayAudioProbe.QueryDisplays();
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
                var route = ApplySavedAudio();
                job = coordinator.ClaimNext(screen, route.MpvDevice, club.ImageSeconds);
                if (job is null)
                {
                    await Task.Delay(400, stoppingToken);
                    continue;
                }

                // hall-player.sh plays the file from disk in the graphical session.
                // A timer is only the fallback when that player is not there.
                var playerReady = await HallPlayerReady(stoppingToken);
                coordinator.SetEngine(playerReady ? "mpv" : "timer");
                var result = playerReady
                    ? await WaitForHallPlayer(job, stoppingToken)
                    : await _timer.PlayAsync(job, coordinator.SetPosition, stoppingToken);
                coordinator.Finish(job, result);
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

    private (string? MpvDevice, string? Sink, string? Port) ApplySavedAudio()
    {
        var fresh = settings.Get();
        var output = DisplayAudioProbe.ResolveOutput(fresh.AudioDevice, DisplayAudioProbe.QueryAudio());
        var route = DisplayAudioProbe.Route(output);
        DisplayAudioProbe.ApplyPort(route.Sink, route.Port, fresh.VolumePercent);
        try
        {
            HallPlayback.AtomicWrite(paths.AudioEnv, HallPlayback.AudioEnv(route.Sink, route.Port, fresh.VolumePercent));
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not write the audio route");
        }

        return route;
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

    private async Task<PlayResult> WaitForHallPlayer(PlayJob job, CancellationToken stoppingToken)
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
        var started = Environment.TickCount64;
        var cap = HallPlayback.PlaybackCap(job.DurationMs, job.Entry.MediaType);
        while (!stoppingToken.IsCancellationRequested)
        {
            var elapsed = Environment.TickCount64 - started;
            coordinator.SetPosition(Math.Min(job.DurationMs, elapsed));
            ApplySavedAudio();
            if (HallPlayback.DoneIs(paths.PlayDone, job.Entry.Id)
                || coordinator.IsFinished(job.Entry.Id)
                || HallPlayback.ClockEnded(paths.PlayClock, job.Entry.Id))
                return PlayResult.Done();
            if (TimeSpan.FromMilliseconds(elapsed) >= cap)
                return PlayResult.Done();
            await Task.Delay(400, stoppingToken);
        }

        return PlayResult.Done();
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
