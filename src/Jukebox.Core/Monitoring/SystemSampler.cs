using System.Globalization;

namespace Jukebox.Core.Monitoring;

public sealed class MonitorSnapshot
{
    public double CpuPercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long DiskUsedBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public long DatabaseBytes { get; init; }
    public int MediaCount { get; init; }
    public int HiddenCount { get; init; }
    public int ErrorCount { get; init; }
    public string? LastScanAt { get; init; }
    public string? LastBackupAt { get; init; }
    public string Version { get; init; } = JukeboxVersion.Current;
    public string Uptime { get; init; } = "";
    public string? PlaybackError { get; init; }
}

public sealed class SystemSampler
{
    private ulong _previousIdle;
    private ulong _previousTotal;
    private double _cpu;
    private long _memUsed;
    private long _memTotal;

    public void Sample()
    {
        if (!OperatingSystem.IsLinux())
            return;
        SampleCpu();
        SampleMemory();
    }

    public MonitorSnapshot Build(string databasePath, int media, int hidden, int errors, string? lastScan, string? lastBackup, DateTime startedUtc, string? playbackError)
    {
        long diskUsed = 0, diskTotal = 0;
        try
        {
            var root = Path.GetPathRoot(databasePath) ?? "/";
            var drive = new DriveInfo(root);
            diskTotal = drive.TotalSize;
            diskUsed = drive.TotalSize - drive.AvailableFreeSpace;
        }
        catch
        {
            // Disk figures stay at zero when the mount cannot be read.
        }

        long databaseBytes = 0;
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
                databaseBytes += new FileInfo(path).Length;
        }

        var uptime = DateTime.UtcNow - startedUtc;
        if (uptime < TimeSpan.Zero)
            uptime = TimeSpan.Zero;

        return new MonitorSnapshot
        {
            CpuPercent = Math.Round(_cpu, 1),
            MemoryUsedBytes = _memUsed,
            MemoryTotalBytes = _memTotal,
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskTotal,
            DatabaseBytes = databaseBytes,
            MediaCount = media,
            HiddenCount = hidden,
            ErrorCount = errors,
            LastScanAt = lastScan,
            LastBackupAt = lastBackup,
            Uptime = $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}",
            PlaybackError = playbackError
        };
    }

    private void SampleCpu()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault(item => item.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
            return;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return;
        ulong idle = ulong.Parse(parts[4], CultureInfo.InvariantCulture);
        ulong total = 0;
        for (var i = 1; i < parts.Length; i++)
            total += ulong.Parse(parts[i], CultureInfo.InvariantCulture);
        if (_previousTotal > 0 && total > _previousTotal)
        {
            var totalDelta = total - _previousTotal;
            var idleDelta = idle - _previousIdle;
            _cpu = (1 - (double)idleDelta / totalDelta) * 100;
        }

        _previousIdle = idle;
        _previousTotal = total;
    }

    private void SampleMemory()
    {
        long total = 0, available = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                total = ParseKb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                available = ParseKb(line);
        }

        _memTotal = total * 1024;
        _memUsed = Math.Max(0, (total - available) * 1024);
    }

    private static long ParseKb(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }
}
