using Jukebox.Core;
using Jukebox.Core.Indexing;

namespace Jukebox.Host;

public sealed class UsbSongCopy(JukeboxPaths paths, MediaIndexer indexer)
{
    public static readonly string[] Hubs = ["/mnt/jukebox-usb", "/media", "/run/media"];

    private readonly object _gate = new();
    private bool _busy;
    private bool _holding;
    private int _done;
    private int _total;
    private int _seen;
    private string _message = "";

    public object Status()
    {
        UsbPackage.Mount();
        var volumes = Volumes();
        var (files, unreadable) = Songs(volumes);

        lock (_gate)
        {
            if (_busy)
            {
                return Body(found: true, canCopy: false, busy: true, UsbSongImport.BusyMessage(_done, _total));
            }

            if (_holding && !unreadable && volumes.Count > 0 && files.Count == _seen)
                return Body(files.Count > 0, files.Count > 0, false, _message);

            _holding = false;
            return Body(files.Count > 0, files.Count > 0, false, UsbSongImport.IdleMessage(volumes.Count, files.Count, unreadable));
        }
    }

    public (bool Ok, int Status, bool Started, object Body) Start()
    {
        UsbPackage.Mount();
        var volumes = Volumes();
        var (files, unreadable) = Songs(volumes);
        if (unreadable && files.Count == 0)
            return (false, StatusCodes.Status409Conflict, false, new { ok = false, message = UsbSongImport.IdleMessage(volumes.Count, 0, true) });

        lock (_gate)
        {
            if (_busy)
                return (true, StatusCodes.Status200OK, false, new { ok = true, message = UsbSongImport.BusyMessage(_done, _total) });
            if (files.Count == 0)
                return (false, StatusCodes.Status409Conflict, false, new { ok = false, message = UsbSongImport.IdleMessage(volumes.Count, 0, false) });

            _busy = true;
            _holding = false;
            _done = 0;
            _total = files.Count;
            _seen = files.Count;
            _message = "Kopieringen er startet. Lad nøglen sidde i.";
        }

        var library = paths.Library;
        _ = Task.Run(() => Run(files, library));
        return (true, StatusCodes.Status200OK, true, new { ok = true, message = "Kopieringen er startet. Lad nøglen sidde i." });
    }

    private void Run(IReadOnlyList<UsbSongImport.SongFile> files, string library)
    {
        UsbSongImport.Report report;
        try
        {
            report = UsbSongImport.Copy(files, library, progress: done =>
            {
                lock (_gate)
                    _done = done;
            });
        }
        catch (Exception)
        {
            report = new UsbSongImport.Report(0, 0, 0, false, true);
        }

        try
        {
            _ = indexer.ReconcileAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // The regular scan still picks the files up.
        }

        lock (_gate)
        {
            _busy = false;
            _holding = true;
            _message = UsbSongImport.DoneMessage(report);
        }
    }

    private static List<string> Volumes()
    {
        var mounts = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists("/proc/mounts"))
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 2 || parts[1].Length == 0)
                    continue;
                mounts.Add(Path.GetFullPath(Unescape(parts[1])));
            }
        }

        return UsbSongImport.Volumes(Hubs, mounts).ToList();
    }

    private static (List<UsbSongImport.SongFile> Files, bool Unreadable) Songs(IReadOnlyList<string> volumes)
    {
        var files = new List<UsbSongImport.SongFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = false;
        foreach (var volume in volumes)
        {
            try
            {
                foreach (var file in UsbSongImport.List(volume))
                {
                    if (seen.Add(file.RelativePath))
                        files.Add(file);
                }
            }
            catch (UnauthorizedAccessException)
            {
                unreadable = true;
            }
        }

        return (files, unreadable && files.Count == 0);
    }

    private static object Body(bool found, bool canCopy, bool busy, string message) => new
    {
        found,
        canCopy,
        busy,
        message
    };

    private static string Unescape(string value) =>
        value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}
