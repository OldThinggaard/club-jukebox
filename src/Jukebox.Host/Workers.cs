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
    private readonly MpvPlayer _mpv = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!indexer.InitialScanCompleted && !stoppingToken.IsCancellationRequested)
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
                var connected = displays.Count(item => item.Connected && item.Width > 0);
                var useMpv = config.PlaybackMode == "mpv" || (config.PlaybackMode == "auto" && connected >= 2 && MpvPlayer.IsInstalled());
                var screen = DisplayAudioProbe.ScreenIndex(displays, club.PlaybackOutput);
                job = coordinator.ClaimNext(screen, string.IsNullOrWhiteSpace(club.AudioDevice) ? null : club.AudioDevice, club.ImageSeconds);
                if (job is null)
                {
                    await Task.Delay(400, stoppingToken);
                    continue;
                }

                IMediaPlayer player = useMpv ? _mpv : _timer;
                coordinator.SetEngine(player.Name);
                var result = await player.PlayAsync(job, coordinator.SetPosition, stoppingToken);
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
