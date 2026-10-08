using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Jukebox.Core.Metadata;

public sealed class FfprobeRunner
{
    public async Task<ProbeTags> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("quiet");
        start.ArgumentList.Add("-print_format");
        start.ArgumentList.Add("json");
        start.ArgumentList.Add("-show_format");
        start.ArgumentList.Add("-show_streams");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffprobe did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        _ = await stderrTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("The file is not a readable media file yet.");
        return Parse(stdout);
    }

    public static ProbeTags Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? title = null, artist = null, album = null, genre = null, date = null;
        long? durationMs = null;
        int? bitrate = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var duration) && double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                durationMs = (long)(seconds * 1000);
            if (format.TryGetProperty("bit_rate", out var bitRate) && int.TryParse(bitRate.GetString(), out var bits))
                bitrate = bits;
            ReadTags(format, ref title, ref artist, ref album, ref genre, ref date);
        }

        int? width = null, height = null;
        string? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var codecType) ? codecType.GetString() : null;
                ReadTags(stream, ref title, ref artist, ref album, ref genre, ref date);
                if (type == "video" && video is null)
                {
                    var codec = stream.TryGetProperty("codec_name", out var codecName) ? codecName.GetString() : null;
                    if (codec is "mjpeg" or "png" or "jpeg")
                        continue;
                    video = codec;
                    if (stream.TryGetProperty("width", out var w))
                        width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h))
                        height = h.GetInt32();
                }
                else if (type == "audio" && audio is null && stream.TryGetProperty("codec_name", out var audioCodec))
                {
                    audio = audioCodec.GetString();
                }
            }
        }

        return new ProbeTags
        {
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            Year = date is null ? null : FilenameParser.FindYear(date),
            DurationMs = durationMs,
            Width = width,
            Height = height,
            Bitrate = bitrate,
            VideoCodec = video,
            AudioCodec = audio
        };
    }

    private static void ReadTags(JsonElement element, ref string? title, ref string? artist, ref string? album, ref string? genre, ref string? date)
    {
        if (!element.TryGetProperty("tags", out var tags))
            return;
        title ??= Tag(tags, "title");
        artist ??= Tag(tags, "artist") ?? Tag(tags, "album_artist");
        album ??= Tag(tags, "album");
        genre ??= Tag(tags, "genre");
        date ??= Tag(tags, "date") ?? Tag(tags, "year");
    }

    private static string? Tag(JsonElement tags, string name)
    {
        foreach (var property in tags.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = property.Value.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }
}
