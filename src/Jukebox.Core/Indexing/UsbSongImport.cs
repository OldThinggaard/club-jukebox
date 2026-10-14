using Jukebox.Core.Domain;
using Jukebox.Core.Metadata;

namespace Jukebox.Core.Indexing;

public static class UsbSongImport
{
    public const long ReserveBytes = 256L * 1024 * 1024;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "$RECYCLE.BIN", "System Volume Information", ".Trash-1000", "#recycle"
    };

    public sealed record SongFile(string SourcePath, string RelativePath);

    public sealed record Report(int Added, int Replaced, int Unchanged, bool DiskFull, bool StickRemoved);

    public static IReadOnlyList<string> Volumes(IEnumerable<string> hubs, IReadOnlySet<string> mountPoints)
    {
        var hits = new List<string>();
        foreach (var hub in hubs)
        {
            if (string.IsNullOrWhiteSpace(hub) || !Directory.Exists(hub))
                continue;
            var root = Path.GetFullPath(hub);
            foreach (var mount in mountPoints)
            {
                if (mount == root || mount.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    hits.Add(mount);
            }
        }

        return hits
            .Where(mount => !hits.Any(other => other.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<SongFile> List(string volume)
    {
        var files = new List<SongFile>();
        if (string.IsNullOrWhiteSpace(volume) || !Directory.Exists(volume))
            return files;

        var root = Path.GetFullPath(volume);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException) when (directory != root)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (name.StartsWith('.') || SkipDirectories.Contains(name))
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (name.Equals("videojukebox.deb", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!MediaTypes.TryGet(entry, out var mediaType) || mediaType == MediaTypes.Image)
                    continue;

                var relative = Path.GetRelativePath(root, entry);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    continue;
                files.Add(new SongFile(entry, relative));
            }
        }

        return files
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Report Copy(IReadOnlyList<SongFile> files, string library, Func<long>? freeSpace = null, long reserveBytes = ReserveBytes, Action<int>? progress = null)
    {
        Directory.CreateDirectory(library);
        var libraryRoot = Path.GetFullPath(library);
        var added = 0;
        var replaced = 0;
        var unchanged = 0;
        var done = 0;
        foreach (var file in files)
        {
            var destination = Path.GetFullPath(Path.Combine(libraryRoot, file.RelativePath));
            if (!destination.StartsWith(libraryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && destination != libraryRoot)
                continue;
            if (!File.Exists(file.SourcePath))
                return new Report(added, replaced, unchanged, false, true);

            var existed = File.Exists(destination);
            if (existed && SameFile(file.SourcePath, destination))
            {
                unchanged++;
                progress?.Invoke(++done);
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(file.SourcePath).Length;
            }
            catch (IOException)
            {
                return new Report(added, replaced, unchanged, false, !File.Exists(file.SourcePath));
            }

            var free = freeSpace?.Invoke() ?? FreeBytes(libraryRoot);
            if (free < length + reserveBytes)
                return new Report(added, replaced, unchanged, true, false);

            var partial = destination + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file.SourcePath, partial, true);
                File.Move(partial, destination, true);
            }
            catch (IOException)
            {
                TryDelete(partial);
                if (!File.Exists(file.SourcePath))
                    return new Report(added, replaced, unchanged, false, true);
                var left = freeSpace?.Invoke() ?? FreeBytes(libraryRoot);
                return new Report(added, replaced, unchanged, left < length + reserveBytes, false);
            }

            if (existed)
                replaced++;
            else
                added++;
            progress?.Invoke(++done);
        }

        return new Report(added, replaced, unchanged, false, false);
    }

    public static string IdleMessage(int volumes, int songs, bool unreadable)
    {
        if (unreadable)
            return "USB-nøglen kunne ikke læses. Tag den ud, og sæt den i igen.";
        if (volumes == 0)
            return "Ingen USB-nøgle er set. Sæt den i, og vent et øjeblik.";
        if (songs == 0)
            return "USB-nøglen er sat i, men der ligger ingen sange på den.";
        return $"USB-nøglen har {Count(songs, "sang", "sange")}. Tryk Kopier sangene.";
    }

    public static string BusyMessage(int done, int total) =>
        $"Kopierer sang {Math.Max(done, 0)} af {Math.Max(total, 0)}. Lad nøglen sidde i.";

    public static string DoneMessage(Report report)
    {
        if (report.DiskFull)
            return report.Added == 0
                ? "Disken er fuld. Ingen sange blev lagt ind. Tag nøglen ud."
                : $"Disken er fuld. {Count(report.Added, "sang", "sange")} blev lagt ind. Tag nøglen ud.";
        if (report.StickRemoved)
            return $"{Count(report.Added, "sang", "sange")} blev lagt ind. Nøglen blev taget ud.";
        if (report.Added == 0 && report.Replaced == 0)
            return "Sangene ligger der allerede. Du kan tage nøglen ud.";
        if (report.Added > 0 && report.Replaced > 0)
            return $"{Count(report.Added, "ny sang", "nye sange")} er lagt ind. {Count(report.Replaced, "sang", "sange")} erstattede en ældre fil. De vises på hylden om et øjeblik. Du kan tage nøglen ud.";
        if (report.Replaced > 0)
            return $"{Count(report.Replaced, "sang", "sange")} erstattede en ældre fil. De vises på hylden om et øjeblik. Du kan tage nøglen ud.";
        return $"{Count(report.Added, "sang", "sange")} er lagt ind. De vises på hylden om et øjeblik. Du kan tage nøglen ud.";
    }

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private static bool SameFile(string source, string destination)
    {
        try
        {
            return new FileInfo(source).Length == new FileInfo(destination).Length
                && SampleHash.Compute(source) == SampleHash.Compute(destination);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static long FreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover part file is skipped on the next copy.
        }
    }
}
