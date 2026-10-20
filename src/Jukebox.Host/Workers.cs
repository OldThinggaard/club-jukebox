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
    private int _screenIndex;
    private long _seenAt = long.MinValue;
    private bool _playerWoken;
    private bool _stoppedWaitingForPlayer;
    private long _playerWaitUntil = long.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var waitUntil = DateTime.UtcNow.AddSeconds(8);
        while (!indexer.InitialScanCompleted && DateTime.UtcNow < waitUntil && !stoppingToken.IsCancellationRequested)
            await Task.Delay(200, stoppingToken);
        RememberScreen();

        while (!stoppingToken.IsCancellationRequested)
        {
            PlayJob? job = null;
            try
            {
                NoteAlive();
                if (HoldQueueForPlayer())
                {
                    await Task.Delay(200, stoppingToken);
                    continue;
                }

                var club = settings.Get();
                // A tap during the pause picture must not wait for a screen probe.
                job = coordinator.ClaimNext(_screenIndex, null, club.ImageSeconds);
                if (job is null)
                {
                    if (coordinator.Halted())
                    {
                        try
                        {
                            HallPlayback.AtomicWrite(paths.PlayRequest, HallPlayback.StopRequest());
                        }
                        catch (IOException)
                        {
                            // The screen has already let the song go.
                        }

                        coordinator.AcknowledgeHalt();
                    }

                    RememberScreen();
                    ApplySavedAudio();
                    await Task.Delay(100, stoppingToken);
                    continue;
                }

                // Tell the television at once. Waiting here left the pause picture up.
                var playerReady = File.Exists("/opt/jukebox/bin/hall-player.sh")
                    && HallPlayback.StampFresh(paths.PlayerStamp, TimeSpan.FromSeconds(15));
                coordinator.SetEngine(playerReady ? "mpv" : "timer");
                coordinator.Publish();
                var outcome = playerReady
                    ? await WaitForHallPlayer(job, stoppingToken)
                    : await WaitForShownMedia(job, stoppingToken);
                job = outcome.Job;
                if (!outcome.Closed)
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

    private void NoteAlive()
    {
        var now = Environment.TickCount64;
        if (_seenAt != long.MinValue && now - _seenAt < 15_000)
            return;
        _seenAt = now;
        try
        {
            PowerGap.Touch(paths.LastSeen);
        }
        catch (IOException)
        {
            // The next pass writes the time. A missed line still counts as running.
        }
    }

    private bool HoldQueueForPlayer()
    {
        var scriptExists = File.Exists("/opt/jukebox/bin/hall-player.sh");
        var stampFresh = HallPlayback.StampFresh(paths.PlayerStamp, TimeSpan.FromSeconds(15));
        if (!HallPlayback.HoldForHallPlayer(scriptExists, stampFresh, _playerWoken, _stoppedWaitingForPlayer))
            return false;
        if (_playerWaitUntil == long.MinValue)
            _playerWaitUntil = Environment.TickCount64 + (long)HallPlayback.HallPlayerWait.TotalMilliseconds;
        if (Environment.TickCount64 >= _playerWaitUntil)
        {
            _stoppedWaitingForPlayer = true;
            return false;
        }

        return true;
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

    private void RememberScreen()
    {
        var now = Environment.TickCount64;
        if (_displaysAt != long.MinValue && now - _displaysAt < 30_000)
            return;
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

        var club = settings.Get();
        if (string.IsNullOrWhiteSpace(club.UiOutput) || string.IsNullOrWhiteSpace(club.PlaybackOutput))
        {
            var (ui, hall) = DisplayAudioProbe.AutoAssign(_displays);
            if (!string.IsNullOrWhiteSpace(ui) && string.IsNullOrWhiteSpace(club.UiOutput))
                settings.Set(SettingKeys.UiOutput, ui);
            if (!string.IsNullOrWhiteSpace(hall) && string.IsNullOrWhiteSpace(club.PlaybackOutput))
                settings.Set(SettingKeys.PlaybackOutput, hall);
            club = settings.Get();
        }

        try
        {
            DisplayAudioProbe.WriteDisplayFile(paths.DisplaysFile, _displays, club.UiOutput, club.PlaybackOutput);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not write the display map");
        }

        _screenIndex = DisplayAudioProbe.ScreenIndex(_displays, club.PlaybackOutput);
    }

    private async Task<(PlayJob Job, PlayResult Result, bool Closed)> WaitForHallPlayer(PlayJob job, CancellationToken stoppingToken)
    {
        if (await WakeHall(job, stoppingToken) is { } woken)
            return woken;
        ArmHall(job);
        if (HaltIfAsked(job) is { } halted)
            return halted;
        var started = Environment.TickCount64;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (HaltIfAsked(job) is { } haltedNow)
                return haltedNow;
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
            {
                var next = coordinator.HandOff(job, true);
                if (next is null)
                    return (job, PlayResult.Done(), true);
                job = next;
                ArmHall(job);
                started = Environment.TickCount64;
                continue;
            }
            var cap = image
                ? HallPlayback.PlaybackCap(job.DurationMs, job.Entry.MediaType)
                : TimeSpan.FromHours(3);
            if (TimeSpan.FromMilliseconds(elapsed) >= cap)
                return (job, PlayResult.Done(), false);
            ApplySavedAudio();
            await Task.Delay(200, stoppingToken);
        }

        return (job, PlayResult.Done(), false);
    }

    private async Task<(PlayJob Job, PlayResult Result, bool Closed)?> WakeHall(PlayJob job, CancellationToken stoppingToken)
    {
        // The player starts empty and says the file is already over. That cuts
        // the first song. A short black film takes that false end. The queue
        // then starts on a player that is already running.
        if (_playerWoken || !File.Exists(HallPlayback.WakeFile))
        {
            _playerWoken = true;
            return null;
        }

        ClearHallSignals();
        HallPlayback.AtomicWrite(paths.PlayRequest, HallPlayback.WakeRequest());
        var started = Environment.TickCount64;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (HaltIfAsked(job) is { } halted)
                return halted;
            var elapsed = Environment.TickCount64 - started;
            if (HallPlayback.EndIsBelievable(elapsed) && HallPlayback.DoneIs(paths.PlayDone, HallPlayback.WakeId))
            {
                _playerWoken = true;
                return null;
            }

            if (elapsed >= 20_000)
            {
                _playerWoken = true;
                return null;
            }

            await Task.Delay(200, stoppingToken);
        }

        return null;
    }

    private void ClearHallSignals()
    {
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
    }

    private void ArmHall(PlayJob job)
    {
        coordinator.MarkFinished(null);
        ClearHallSignals();

        var art = job.Entry.ThumbnailPath;
        if (string.IsNullOrWhiteSpace(art) || !File.Exists(art))
            art = "";
        HallPlayback.AtomicWrite(paths.PlayRequest, HallPlayback.Request(job.Entry.Id, job.AbsolutePath, job.Entry.MediaType, job.DurationMs, art));
    }

    private (PlayJob Job, PlayResult Result, bool Closed)? HaltIfAsked(PlayJob job)
    {
        if (!coordinator.Halted())
            return null;
        try
        {
            HallPlayback.AtomicWrite(paths.PlayRequest, HallPlayback.StopRequest());
        }
        catch (IOException)
        {
            // The screen has already let the song go.
        }

        coordinator.AcknowledgeHalt();
        return (job, PlayResult.Stopped(), true);
    }

    private async Task<(PlayJob Job, PlayResult Result, bool Closed)> WaitForShownMedia(PlayJob job, CancellationToken stoppingToken)
    {
        var started = Environment.TickCount64;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (HaltIfAsked(job) is { } haltedNow)
                return haltedNow;
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
            {
                var next = coordinator.HandOff(job, true);
                if (next is null)
                    return (job, PlayResult.Done(), true);
                job = next;
                started = Environment.TickCount64;
                continue;
            }
            await Task.Delay(200, stoppingToken);
        }

        return (job, PlayResult.Done(), false);
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
