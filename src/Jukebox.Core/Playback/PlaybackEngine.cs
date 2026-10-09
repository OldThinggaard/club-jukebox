using System.Diagnostics;
using Jukebox.Core.Data;
using Jukebox.Core.Domain;

namespace Jukebox.Core.Playback;

public sealed class PlayJob
{
    public required QueueEntry Entry { get; init; }
    public required string AbsolutePath { get; init; }
    public int ScreenIndex { get; init; }
    public string? AudioDevice { get; init; }
    public long DurationMs { get; init; }
}

public sealed class PlayResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public static PlayResult Done() => new() { Ok = true };
    public static PlayResult Failed(string error) => new() { Ok = false, Error = error };
}

public interface IMediaPlayer
{
    string Name { get; }
    Task<PlayResult> PlayAsync(PlayJob job, Action<long> position, CancellationToken cancellationToken);
}

public sealed class TimerPlayer : IMediaPlayer
{
    public string Name => "timer";

    public async Task<PlayResult> PlayAsync(PlayJob job, Action<long> position, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < job.DurationMs)
        {
            position(Math.Min(job.DurationMs, Environment.TickCount64 - started));
            await Task.Delay(200, cancellationToken);
        }

        position(job.DurationMs);
        return PlayResult.Done();
    }
}

public sealed class MpvPlayer : IMediaPlayer
{
    public string Name => "mpv";

    public static bool IsInstalled()
    {
        try
        {
            var start = new ProcessStartInfo("mpv", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            process?.WaitForExit(1500);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    public async Task<PlayResult> PlayAsync(PlayJob job, Action<long> position, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("mpv")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--no-terminal");
        start.ArgumentList.Add("--really-quiet");
        start.ArgumentList.Add("--fs");
        start.ArgumentList.Add("--no-border");
        start.ArgumentList.Add("--ontop");
        start.ArgumentList.Add("--no-osc");
        start.ArgumentList.Add("--hwdec=auto-safe");
        start.ArgumentList.Add($"--fs-screen={job.ScreenIndex}");
        if (!string.IsNullOrWhiteSpace(job.AudioDevice))
            start.ArgumentList.Add($"--audio-device={job.AudioDevice}");

        if (job.Entry.MediaType == MediaTypes.Audio)
        {
            var art = job.Entry.ThumbnailPath;
            if (string.IsNullOrWhiteSpace(art) || !File.Exists(art))
                return await new TimerPlayer().PlayAsync(job, position, cancellationToken);
            var seconds = Math.Max(1, job.DurationMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.ArgumentList.Add($"--image-display-duration={seconds}");
            start.ArgumentList.Add($"--audio-file={job.AbsolutePath}");
            start.ArgumentList.Add(art);
        }
        else if (job.Entry.MediaType == MediaTypes.Image)
        {
            var seconds = Math.Max(1, job.DurationMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.ArgumentList.Add($"--image-display-duration={seconds}");
            start.ArgumentList.Add(job.AbsolutePath);
        }
        else
        {
            start.ArgumentList.Add(job.AbsolutePath);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("mpv did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var started = Environment.TickCount64;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        });

        while (!process.HasExited)
        {
            position(Math.Min(job.DurationMs, Environment.TickCount64 - started));
            await Task.Delay(250, stop.Token);
        }

        position(Math.Min(job.DurationMs, Environment.TickCount64 - started));
        return process.ExitCode == 0 ? PlayResult.Done() : PlayResult.Failed($"mpv exited with code {process.ExitCode}");
    }
}

public sealed class PlaybackCoordinator
{
    private readonly QueueStore _queue;
    private readonly MediaStore _media;
    private readonly SettingsStore _settings;
    private readonly object _gate = new();
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private DateTime _hallHeartbeatUtc = DateTime.MinValue;
    private long _positionMs;
    private PlayJob? _current;
    private string _engine = "timer";
    private string? _lastError;

    public PlaybackCoordinator(QueueStore queue, MediaStore media, SettingsStore settings)
    {
        _queue = queue;
        _media = media;
        _settings = settings;
    }

    public void Touch()
    {
        lock (_gate)
            _lastActivityUtc = DateTime.UtcNow;
    }

    public void HallHeartbeat()
    {
        lock (_gate)
            _hallHeartbeatUtc = DateTime.UtcNow;
    }

    public bool HallIsAlive =>
        DateTime.UtcNow - _hallHeartbeatUtc < TimeSpan.FromSeconds(6);

    public void SetEngine(string name)
    {
        lock (_gate)
            _engine = name;
    }

    public void SetPosition(long position)
    {
        lock (_gate)
            _positionMs = position;
    }

    public PlaybackSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PlaybackSnapshot(_current, _positionMs, _engine, _lastActivityUtc, _lastError);
        }
    }

    public PlayJob? ClaimNext(int screenIndex, string? audioDevice, int imageSeconds)
    {
        lock (_gate)
        {
            if (_current is not null)
                return null;

            var next = _queue.ClaimOldest();
            if (next is null)
            {
                next = MaybeAutoplay();
            }

            if (next is null)
                return null;

            var source = _media.ListSources().FirstOrDefault(item => item.Id == _media.Get(next.MediaId)?.SourceId);
            var item = _media.Get(next.MediaId);
            if (item is null || source is null)
            {
                _queue.Complete(next.Id, "abandoned");
                _lastError = "The queued file is no longer in the library.";
                return null;
            }

            var path = Path.GetFullPath(Path.Combine(source.Path, item.RelativePath));
            var duration = item.MediaType == MediaTypes.Image
                ? imageSeconds * 1000L
                : Math.Max(1000, item.DurationMs ?? 10_000);
            var job = new PlayJob
            {
                Entry = next,
                AbsolutePath = path,
                ScreenIndex = screenIndex,
                AudioDevice = audioDevice,
                DurationMs = duration
            };
            if (!File.Exists(path))
            {
                _queue.Complete(next.Id, "abandoned");
                _lastError = "The file could not be opened.";
                return null;
            }

            _current = job;
            _positionMs = 0;
            _lastError = null;
            return job;
        }
    }

    public void Finish(PlayJob job, PlayResult result)
    {
        lock (_gate)
        {
            _queue.Complete(job.Entry.Id, result.Ok ? "completed" : "abandoned");
            if (result.Ok)
                _media.RecordPlay(job.Entry.MediaId, job.Entry.Origin, true, job.Entry.Title, job.Entry.Artist);
            else
                _lastError = result.Error;
            if (_current?.Entry.Id == job.Entry.Id)
                _current = null;
            _positionMs = 0;
        }
    }

    private QueueEntry? MaybeAutoplay()
    {
        var advertisement = _media.Events(DateTime.UtcNow)
            .Where(item => item.MediaType is MediaTypes.Video or MediaTypes.Audio or MediaTypes.Image)
            .OrderBy(item => string.IsNullOrEmpty(item.LastAutoPlayedAt) ? "" : item.LastAutoPlayedAt)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (advertisement is not null)
        {
            var queuedAd = _queue.TryEnqueue(advertisement.Id, Origins.Auto, "reklame");
            if (!queuedAd.Ok)
                return null;
            return _queue.ClaimOldest();
        }

        var settings = _settings.Get();
        if (settings.IdleTimeoutSeconds <= 0)
            return null;
        if (DateTime.UtcNow - _lastActivityUtc < TimeSpan.FromSeconds(settings.IdleTimeoutSeconds))
            return null;

        var candidates = _media.AutoplayCandidates();
        if (candidates.Count == 0)
            return null;

        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, settings.CooldownMinutes));
        var recent = _media.RecentHumanPlays(cutoff);
        var fresh = candidates.Where(item =>
            !recent.Contains(item.Id)
            && (string.IsNullOrWhiteSpace(item.LastAutoPlayedAt) || OlderThan(item.LastAutoPlayedAt, cutoff))).ToList();
        var pool = fresh.Count > 0 ? fresh : candidates.ToList();
        var strategy = settings.IdleStrategy;
        MediaItem pick;
        var least = strategy == "least" || (strategy == "hybrid" && Random.Shared.Next(100) < 80);
        if (strategy == "random")
            least = false;
        pick = least ? pool[0] : pool[Random.Shared.Next(pool.Count)];
        var enqueued = _queue.TryEnqueue(pick.Id, Origins.Auto, "idle");
        if (!enqueued.Ok)
            return null;
        return _queue.ClaimOldest();
    }

    private static bool OlderThan(string? iso, DateTime cutoff) =>
        string.IsNullOrWhiteSpace(iso) || ClubCulture.ParseIso(iso) < cutoff;
}

public sealed record PlaybackSnapshot(PlayJob? Current, long PositionMs, string Engine, DateTime LastActivityUtc, string? LastError);
