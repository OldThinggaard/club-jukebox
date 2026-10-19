using System.Net;
using System.Net.Http.Headers;
using Jukebox.Core.Domain;

namespace Jukebox.Core.Onlime;

public sealed record OnlimeFetchReport(int Added, int Skipped, int Failed, bool DiskFull, string Message);

public sealed class OnlimeShareClient : IDisposable
{
    public const long ReserveBytes = 256L * 1024 * 1024;
    public const int SongCap = 8000;

    private readonly HttpClient _http;
    private readonly string _grpcBase;

    public OnlimeShareClient(HttpMessageHandler handler, string grpcBase = "https://grpc.jotta.cloud")
    {
        _grpcBase = grpcBase.TrimEnd('/');
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<OnlimeFetchReport> FetchAsync(
        OnlimeShareLink link,
        string destination,
        IEnumerable<string> alreadyThere,
        CancellationToken cancellationToken,
        Action<int, int>? progress = null,
        Func<long>? freeSpace = null)
    {
        Lookup lookup;
        try
        {
            lookup = await LookupAsync(link.ShareId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new OnlimeFetchReport(0, 0, 0, false, "Maskinen kunne ikke nå Onlime. Sæt kablet i, og prøv igen.");
        }

        if (lookup.Token is null)
            return new OnlimeFetchReport(0, 0, 0, false, lookup.Message ?? "Linket blev ikke fundet.");

        IReadOnlyList<RemoteSong> songs;
        try
        {
            songs = await ListSongsAsync(lookup.Token, link.FolderPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new OnlimeFetchReport(0, 0, 0, false, "Mappen kunne ikke læses. Prøv igen.");
        }

        Directory.CreateDirectory(destination);
        var known = new HashSet<string>(alreadyThere, StringComparer.OrdinalIgnoreCase);
        foreach (var name in NamesUnder(destination))
            known.Add(name);

        var added = 0;
        var skipped = 0;
        var failed = 0;
        var diskFull = false;
        var done = 0;
        progress?.Invoke(0, songs.Count);
        foreach (var song in songs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeSongName(song.Name) || known.Contains(song.Name))
            {
                skipped++;
                progress?.Invoke(++done, songs.Count);
                continue;
            }

            var target = Path.Combine(destination, song.Name);
            var partial = target + ".part";
            try
            {
                var length = await DownloadAsync(song.Url, lookup.Token, partial, song.Size, freeSpace, cancellationToken);
                if (length < 0)
                {
                    diskFull = true;
                    TryDelete(partial);
                    break;
                }

                if (File.Exists(target))
                {
                    TryDelete(partial);
                    skipped++;
                    known.Add(song.Name);
                }
                else
                {
                    File.Move(partial, target);
                    known.Add(song.Name);
                    added++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(partial);
                throw;
            }
            catch (Exception)
            {
                TryDelete(partial);
                failed++;
            }

            progress?.Invoke(++done, songs.Count);
        }

        return new OnlimeFetchReport(added, skipped, failed, diskFull, Describe(added, skipped, failed, diskFull, songs.Count >= SongCap && !diskFull));
    }

    public void Dispose() => _http.Dispose();

    public static IEnumerable<string> NamesUnder(params string[] directories)
    {
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.Length == 0 || name.StartsWith('.') || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return name;
            }
        }
    }

    private async Task<Lookup> LookupAsync(string shareId, CancellationToken cancellationToken)
    {
        var (message, status, _) = await PostAsync(
            "/no.jotta.openapi.sharing.v2.PublicSharingService/LookupShare",
            OnlimeProto.LookupRequest(shareId),
            bearer: null,
            cancellationToken);
        if (status != 0 || message is null)
            return new Lookup(null, StatusMessage(status));

        string? token = null;
        var challenge = false;
        int? error = null;
        foreach (var field in OnlimeProto.Read(message))
        {
            if (field.Number == 1 && field.Wire == 2)
                challenge = true;
            else if (field.Number == 2 && field.Wire == 2)
                token = Token(field.Bytes);
            else if (field.Number == 3 && field.Wire == 2)
                error = ErrorKind(field.Bytes);
        }

        if (!string.IsNullOrWhiteSpace(token))
            return new Lookup(token, null);
        if (challenge)
            return new Lookup(null, "Linket har en adgangskode. Slå adgangskoden fra, når du deler mappen.");
        return new Lookup(null, ErrorMessage(error));
    }

    private async Task<IReadOnlyList<RemoteSong>> ListSongsAsync(string token, string folderPath, CancellationToken cancellationToken)
    {
        var songs = new List<RemoteSong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(string.IsNullOrWhiteSpace(folderPath) ? "/" : folderPath);
        while (pending.Count > 0 && songs.Count < SongCap)
        {
            var path = pending.Dequeue();
            if (!seen.Add(path))
                continue;
            string? cursor = null;
            var pages = 0;
            do
            {
                var (message, status, _) = await PostAsync(
                    "/no.jotta.openapi.file.v2.FileService/GetPath",
                    OnlimeProto.GetPathRequest(path, cursor),
                    token,
                    cancellationToken);
                if (status != 0 || message is null)
                    throw new InvalidDataException("GetPath failed.");
                var page = ReadPage(message);
                if (page.Item is not null)
                    Collect(page.Item, pending, seen, songs);
                cursor = string.IsNullOrWhiteSpace(page.Cursor) || page.Cursor == cursor ? null : page.Cursor;
                pages++;
            }
            while (cursor is not null && pages < 40 && songs.Count < SongCap);
        }

        return songs;
    }

    private static void Collect(PathNode item, Queue<string> pending, HashSet<string> seen, List<RemoteSong> songs)
    {
        if (IsFolder(item))
        {
            foreach (var child in item.Children)
                Collect(child, pending, seen, songs);
            if (item.Children.Count == 0 && !string.IsNullOrWhiteSpace(item.Path) && item.Path != "/" && !seen.Contains(item.Path))
                pending.Enqueue(item.Path);
            return;
        }

        if (songs.Count >= SongCap || !SafeSongName(item.Name) || !OnlimeShareLink.AllowedHost(HostOf(item.Download)))
            return;
        songs.Add(new RemoteSong(item.Name, item.Size, item.Download));
    }

    private async Task<long> DownloadAsync(string url, string token, string partial, long listedSize, Func<long>? freeSpace, CancellationToken cancellationToken)
    {
        if (listedSize > 0 && !RoomFor(partial, listedSize, freeSpace))
            return -1;

        var current = WithToken(url, token);
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location;
                if (next is null)
                    throw new InvalidDataException("Missing redirect.");
                if (!next.IsAbsoluteUri)
                    next = new Uri(current, next);
                if (!AllowedDownload(next))
                    throw new InvalidDataException("Redirect left Onlime.");
                current = next;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? (listedSize > 0 ? listedSize : (long?)null);
            if (length is not null && !RoomFor(partial, length.Value, freeSpace))
                return -1;
            if (length is null && !RoomFor(partial, 0, freeSpace))
                return -1;

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
            return output.Length;
        }

        throw new InvalidDataException("Too many redirects.");
    }

    private async Task<(byte[]? Message, int Status, string GrpcMessage)> PostAsync(string path, byte[] message, string? bearer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(HttpMethod.Post, _grpcBase + path);
        request.Content = new ByteArrayContent(OnlimeProto.Frame(message));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
        request.Headers.TryAddWithoutValidation("Accept", "application/grpc-web+proto");
        request.Headers.TryAddWithoutValidation("X-Grpc-Web", "1");
        if (!string.IsNullOrEmpty(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await _http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        if (body.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Onlime svaret var for stort.");
        var opened = OnlimeProto.OpenFrame(body);
        if (response.Headers.TryGetValues("grpc-status", out var header) && int.TryParse(header.FirstOrDefault(), out var code))
            return (opened.Message, code, opened.GrpcMessage);
        return opened;
    }

    private static string? Token(byte[] granted)
    {
        foreach (var field in OnlimeProto.Read(granted))
        {
            if (field.Number != 1 || field.Wire != 2)
                continue;
            foreach (var inner in OnlimeProto.Read(field.Bytes))
            {
                if (inner.Number == 1 && inner.Wire == 2)
                {
                    var token = OnlimeProto.Text(inner.Bytes).Trim();
                    if (token.Length > 0)
                        return token;
                }
            }
        }

        return null;
    }

    private static int? ErrorKind(byte[] error)
    {
        foreach (var field in OnlimeProto.Read(error))
        {
            if (field.Number == 1 && field.Wire == 0)
                return (int)field.Varint;
        }

        return null;
    }

    private static (PathNode? Item, string Cursor) ReadPage(byte[] message)
    {
        PathNode? item = null;
        var cursor = "";
        foreach (var field in OnlimeProto.Read(message))
        {
            if (field.Number == 1 && field.Wire == 2)
                item = ReadNode(field.Bytes);
            else if (field.Number == 2 && field.Wire == 2)
                cursor = OnlimeProto.Text(field.Bytes);
        }

        return (item, cursor);
    }

    private static PathNode ReadNode(byte[] bytes)
    {
        var path = "";
        var name = "";
        var download = "";
        var type = 0;
        long size = 0;
        var children = new List<PathNode>();
        foreach (var field in OnlimeProto.Read(bytes))
        {
            switch (field.Number)
            {
                case 1 when field.Wire == 2:
                    path = OnlimeProto.Text(field.Bytes);
                    break;
                case 2 when field.Wire == 2:
                    name = OnlimeProto.Text(field.Bytes);
                    break;
                case 4 when field.Wire == 0:
                    type = (int)field.Varint;
                    break;
                case 6 when field.Wire == 2:
                    download = OnlimeProto.Text(field.Bytes);
                    break;
                case 7 when field.Wire == 0:
                    size = field.Varint > long.MaxValue ? long.MaxValue : (long)field.Varint;
                    break;
                case 13 when field.Wire == 2:
                    children.Add(ReadNode(field.Bytes));
                    break;
            }
        }

        if (name.Length == 0)
            name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        return new PathNode(path, name, type, size, download, children);
    }

    private static bool IsFolder(PathNode item) =>
        item.Type == 2 || (item.Type == 0 && item.Download.Length == 0 && item.Children.Count > 0);

    public static bool SafeSongName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.'))
            return false;
        if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        return MediaTypes.TryGet(name, out var mediaType) && mediaType != MediaTypes.Image;
    }

    private static Uri WithToken(string url, string token)
    {
        var uri = new Uri(url);
        if (!AllowedDownload(uri))
            throw new InvalidDataException("Download left Onlime.");
        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        if (!query.Split('&', StringSplitOptions.RemoveEmptyEntries).Any(part => part.StartsWith("access_token=", StringComparison.Ordinal)))
        {
            var encoded = Uri.EscapeDataString(token);
            builder.Query = string.IsNullOrEmpty(query) ? "access_token=" + encoded : query + "&access_token=" + encoded;
        }

        return builder.Uri;
    }

    private static bool AllowedDownload(Uri uri) =>
        uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && OnlimeShareLink.AllowedHost(uri.IdnHost);

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost : "";

    private static string StatusMessage(int status) => status switch
    {
        3 => "Linket er ikke et offentligt Onlime-link.",
        5 => "Linket blev ikke fundet. Tjek at mappen er delt med offentlig adgang.",
        16 => "Onlime afviste linket. Tjek at mappen er offentlig.",
        _ => "Onlime svarede ikke. Prøv igen."
    };

    private static string ErrorMessage(int? kind) => kind switch
    {
        2 => "Onlime har lukket for flere hentninger på dette link.",
        3 or 4 => "Linket er slået fra i Onlime.",
        5 => "Linket er udløbet.",
        _ => "Linket blev ikke fundet. Tjek at mappen er delt med offentlig adgang."
    };

    public static string Describe(int added, int skipped, int failed, bool diskFull, bool capped)
    {
        if (diskFull && added == 0)
            return "Disken er fuld. Ingen nye sange blev hentet.";
        if (diskFull)
            return $"Disken er fuld. {Count(added)} blev hentet.";
        if (added == 0 && failed == 0)
            return skipped == 0 ? "Mappen har ingen sange." : "Ingen nye sange. De ligger allerede på maskinen.";
        if (failed > 0 && added == 0)
            return "Sangene kunne ikke hentes. Prøv igen.";
        var text = $"{Count(added)} er hentet. De vises på hylden om et øjeblik.";
        if (skipped > 0)
            text += " Resten lå allerede på maskinen.";
        if (failed > 0)
            text += " Nogle sange kunne ikke hentes.";
        if (capped)
            text += " Mappen er meget stor, så hentningen stoppede.";
        return text;
    }

    private static string Count(int count) => count == 1 ? "1 sang" : $"{count} sange";

    private static bool RoomFor(string partial, long bytes, Func<long>? freeSpace)
    {
        var directory = Path.GetDirectoryName(partial)!;
        var free = freeSpace?.Invoke() ?? FreeBytes(directory);
        return free >= bytes + ReserveBytes;
    }

    private static long FreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (Exception)
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
            // The next fetch replaces a leftover part file.
        }
    }

    private sealed record Lookup(string? Token, string? Message);

    private sealed record RemoteSong(string Name, long Size, string Url);

    private sealed record PathNode(string Path, string Name, int Type, long Size, string Download, List<PathNode> Children);
}
