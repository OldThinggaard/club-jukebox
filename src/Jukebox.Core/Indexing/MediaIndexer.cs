using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Jukebox.Core.Metadata;
using Jukebox.Core.Thumbnails;
using Microsoft.Extensions.Logging;

namespace Jukebox.Core.Indexing;

public sealed class MediaIndexer
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "$RECYCLE.BIN", "System Volume Information", ".Trash-1000", "#recycle"
    };

    private readonly MediaStore _media;
    private readonly SettingsStore _settings;
    private readonly FfprobeRunner _ffprobe;
    private readonly ThumbnailGenerator _thumbnails;
    private readonly ILogger<MediaIndexer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MediaIndexer(MediaStore media, SettingsStore settings, FfprobeRunner ffprobe, ThumbnailGenerator thumbnails, ILogger<MediaIndexer> logger)
    {
        _media = media;
        _settings = settings;
        _ffprobe = ffprobe;
        _thumbnails = thumbnails;
        _logger = logger;
    }

    public bool InitialScanCompleted { get; private set; }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return;
        try
        {
            var sources = _media.ListSources().Where(source => source.Enabled).OrderBy(source => source.Priority).ToList();
            var scanned = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(source.Path))
                {
                    _logger.LogInformation("Source {Name} is not mounted at {Path}", source.Name, source.Path);
                    continue;
                }

                scanned.Add(source.Id);
                foreach (var file in Enumerate(source.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source.Path, file);
                    if (relative.Contains("..", StringComparison.Ordinal))
                        continue;
                    seen.Add(MediaStore.Key(source.Id, relative));
                    await ProcessAsync(source, file, relative, cancellationToken);
                }
            }

            _media.RemoveUnseen(scanned, seen);
            _settings.Set(SettingKeys.LastScanAt, ClubCulture.Iso(DateTime.UtcNow));
        }
        finally
        {
            InitialScanCompleted = true;
            _gate.Release();
        }
    }

    private async Task ProcessAsync(MediaSource source, string file, string relative, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0)
            return;

        var existing = _media.FindByPath(source.Id, relative);
        if (existing is { IsReady: true, ForceReindex: false } && existing.FileSize == info.Length && existing.ModifiedUnix == ToUnix(info.LastWriteTimeUtc))
            return;

        var size = info.Length;
        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
        info.Refresh();
        if (!info.Exists || info.Length != size || info.Length == 0)
        {
            _logger.LogInformation("Waiting for {File} to finish copying", relative);
            return;
        }

        string hash;
        try
        {
            hash = SampleHash.Compute(file);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {File}", relative);
            return;
        }

        if (existing is { IsReady: true, ForceReindex: false } && existing.SampleHash == hash && existing.FileSize == info.Length)
        {
            _media.TouchFile(existing.Id, info.Length, ToUnix(info.LastWriteTimeUtc), hash);
            return;
        }

        var parts = FilenameParser.Parse(info.Name);
        if (!MediaTypes.TryGet(file, out var mediaType))
            return;

        try
        {
            var probe = await _ffprobe.ProbeAsync(file, cancellationToken);
            var priorOverride = existing is null ? null : _media.GetOverride(existing.Id);
            var resolved = MetadataMerger.Resolve(probe, parts, priorOverride, info.Name);
            var thumb = await _thumbnails.CreateAsync(file, mediaType, probe, resolved.Title, resolved.Artist, cancellationToken);
            var categories = Categorizer.Build(mediaType, resolved.Genre, resolved.Year, resolved.Title, resolved.Artist, resolved.Album);
            _media.SaveIndexed(new IndexedSave
            {
                SourceId = source.Id,
                RelativePath = relative,
                FileName = info.Name,
                MediaType = mediaType,
                LibraryRole = source.Role,
                FileSize = info.Length,
                ModifiedUnix = ToUnix(info.LastWriteTimeUtc),
                SampleHash = hash,
                Probe = probe,
                FileNameParts = parts,
                Resolved = resolved,
                ThumbnailPath = thumb,
                Ready = true,
                HasOverride = priorOverride is not null
            }, categories);
            if (existing?.ThumbnailPath is not null && existing.ThumbnailPath != thumb)
                TryDelete(existing.ThumbnailPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Indexing failed for {File}", relative);
            _media.SaveIndexed(new IndexedSave
            {
                SourceId = source.Id,
                RelativePath = relative,
                FileName = info.Name,
                MediaType = mediaType,
                LibraryRole = source.Role,
                FileSize = info.Length,
                ModifiedUnix = ToUnix(info.LastWriteTimeUtc),
                SampleHash = hash,
                FileNameParts = parts,
                Resolved = new ResolvedMetadata
                {
                    Title = parts.Title ?? info.Name,
                    Artist = parts.Artist ?? "",
                    ArtistLetter = ClubCulture.Letter(parts.Artist),
                    Year = parts.Year,
                    Decade = ClubCulture.DecadeLabel(parts.Year)
                },
                Ready = false,
                Error = Trim(ex.Message)
            }, []);
        }
    }

    private static IEnumerable<string> Enumerate(string root)
    {
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
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (name.StartsWith('.') || SkipDirectories.Contains(name))
                        continue;
                    pending.Push(entry);
                    continue;
                }

                if (name.StartsWith('.') || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (MediaTypes.TryGet(entry, out _))
                    yield return entry;
            }
        }
    }

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A leftover thumbnail is harmless.
        }
    }

    private static string Trim(string message) => message.Length <= 400 ? message : message[..400];
}
