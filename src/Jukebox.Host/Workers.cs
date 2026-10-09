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

                // The picture and the sound are started by hall-player.sh in the
                // graphical session. This timer only keeps the queue moving.
                coordinator.SetEngine("mpv");
                var playing = _timer.PlayAsync(job, coordinator.SetPosition, stoppingToken);
                while (!playing.IsCompleted)
                {
                    ApplySavedAudio();
                    await Task.WhenAny(playing, Task.Delay(2000, stoppingToken));
                }

                var result = await playing;
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
        return route;
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
