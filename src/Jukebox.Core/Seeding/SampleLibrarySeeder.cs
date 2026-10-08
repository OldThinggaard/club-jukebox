using System.Diagnostics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace Jukebox.Core.Seeding;

public static class SampleLibrarySeeder
{
    private sealed record Sample(string RelativeFolder, string FileName, string Title, string Artist, string Album, string Genre, string Year, bool Audio, string Color);

    public static async Task SeedAsync(JukeboxPaths paths, CancellationToken cancellationToken)
    {
        if (Directory.EnumerateFiles(paths.OnlimeMedia, "*", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(paths.Library, "*", SearchOption.AllDirectories).Any())
            return;

        var samples = new[]
        {
            new Sample("onlime", "Queen - Bohemian Rhapsody.mp4", "Bohemian Rhapsody", "Queen", "A Night at the Opera", "Rock", "1975", false, "#6B2D3C"),
            new Sample("onlime", "Queen - Radio Ga Ga.mp4", "Radio Ga Ga", "Queen", "The Works", "Rock", "1984", false, "#3C4C6B"),
            new Sample("onlime", "Queen - Bohemian Rhapsody Live.mp4", "Bohemian Rhapsody Live", "Queen", "Live at Wembley", "Rock", "1986", false, "#6B3C2D"),
            new Sample("onlime", "ABBA - Dancing Queen.mp4", "Dancing Queen", "ABBA", "Arrival", "Pop", "1976", false, "#6B5A2D"),
            new Sample("onlime", "Beatles - Hey Jude.mp4", "Hey Jude", "Beatles", "Hey Jude", "Rock", "1968", false, "#2D4C3C"),
            new Sample("onlime", "Coldplay - Yellow.mp4", "Yellow", "Coldplay", "Parachutes", "Alternative", "2000", false, "#6B6A2D"),
            new Sample("onlime", "Adele - Rolling in the Deep.mp4", "Rolling in the Deep", "Adele", "21", "Soul", "2011", false, "#4C2D4C"),
            new Sample("onlime", "Bon Jovi - Livin on a Prayer.mp4", "Livin on a Prayer", "Bon Jovi", "Slippery When Wet", "Rock", "1986", false, "#2D3C6B"),
            new Sample("library", "Kim Larsen - Papirsklip.mp3", "Papirsklip", "Kim Larsen", "Midt om natten", "Dansk Pop", "1983", true, "#3C3C2D")
        };

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = sample.RelativeFolder == "onlime" ? paths.OnlimeMedia : paths.Library;
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, sample.FileName);
            var cover = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                DrawCover(cover, sample.Title, sample.Artist, sample.Color);
                if (sample.Audio)
                    await WriteAudioAsync(cover, destination, sample, cancellationToken);
                else
                    await WriteVideoAsync(cover, destination, sample, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Sample {sample.FileName} was skipped: {ex.Message}");
            }
            finally
            {
                if (File.Exists(cover))
                    File.Delete(cover);
            }
        }

        DrawPoster(Path.Combine(paths.OnlimeEvents, "Friday Bar.png"), "Friday bar", "This week in the clubhouse");
        DrawPoster(Path.Combine(paths.OnlimeEvents, "Club Championship.png"), "Club championship", "Saturday from 09:00");
        DrawPoster(Path.Combine(paths.OnlimeEvents, "House Sponsor.png"), "House sponsor", "Thank you for supporting the club");
        DrawLogo(paths.LogoPath);
    }

    private static void DrawCover(string path, string title, string artist, string color)
    {
        using var image = new Image<Rgba32>(640, 360, Color.ParseHex("#14120F"));
        var titleFont = Font(42, FontStyle.Bold);
        var artistFont = Font(24, FontStyle.Regular);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex(color), new RectangleF(0, 0, 640, 12));
            ctx.DrawText(title, titleFont, Color.ParseHex("#F4EFE6"), new PointF(36, 120));
            ctx.DrawText(artist, artistFont, Color.ParseHex("#E2B15A"), new PointF(36, 190));
        });
        image.SaveAsPng(path);
    }

    private static void DrawPoster(string path, string title, string detail)
    {
        using var image = new Image<Rgba32>(1280, 720, Color.ParseHex("#1A1714"));
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("#E2B15A"), new RectangleF(80, 80, 1120, 6));
            ctx.DrawText(title, Font(72, FontStyle.Bold), Color.ParseHex("#F4EFE6"), new PointF(80, 220));
            ctx.DrawText(detail, Font(32, FontStyle.Regular), Color.ParseHex("#E2B15A"), new PointF(80, 340));
        });
        image.SaveAsPng(path);
    }

    private static void DrawLogo(string path)
    {
        using var image = new Image<Rgba32>(512, 512, Color.ParseHex("#14120F"));
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("#E2B15A"), new RectangleF(48, 48, 416, 416));
            ctx.Fill(Color.ParseHex("#14120F"), new RectangleF(72, 72, 368, 368));
            ctx.DrawText("O&I", Font(120, FontStyle.Bold), Color.ParseHex("#E2B15A"), new PointF(110, 170));
        });
        image.SaveAsPng(path);
    }

    private static async Task WriteVideoAsync(string cover, string destination, Sample sample, CancellationToken cancellationToken)
    {
        var frequency = 220 + (Math.Abs(sample.Title.GetHashCode()) % 400);
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-loop");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(cover);
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("lavfi");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add($"sine=frequency={frequency}:duration=12");
        start.ArgumentList.Add("-c:v");
        start.ArgumentList.Add("libx264");
        start.ArgumentList.Add("-preset");
        start.ArgumentList.Add("ultrafast");
        start.ArgumentList.Add("-tune");
        start.ArgumentList.Add("stillimage");
        start.ArgumentList.Add("-pix_fmt");
        start.ArgumentList.Add("yuv420p");
        start.ArgumentList.Add("-c:a");
        start.ArgumentList.Add("aac");
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("12");
        start.ArgumentList.Add("-shortest");
        AddMetadata(start, sample);
        start.ArgumentList.Add(destination);
        await RunAsync(start, cancellationToken);
    }

    private static async Task WriteAudioAsync(string cover, string destination, Sample sample, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("lavfi");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("sine=frequency=330:duration=12");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(cover);
        start.ArgumentList.Add("-map");
        start.ArgumentList.Add("0:a");
        start.ArgumentList.Add("-map");
        start.ArgumentList.Add("1:v");
        start.ArgumentList.Add("-c:a");
        start.ArgumentList.Add("libmp3lame");
        start.ArgumentList.Add("-c:v");
        start.ArgumentList.Add("mjpeg");
        start.ArgumentList.Add("-id3v2_version");
        start.ArgumentList.Add("3");
        start.ArgumentList.Add("-metadata:s:v");
        start.ArgumentList.Add("title=Album cover");
        start.ArgumentList.Add("-metadata:s:v");
        start.ArgumentList.Add("comment=Cover (front)");
        AddMetadata(start, sample);
        start.ArgumentList.Add(destination);
        await RunAsync(start, cancellationToken);
    }

    private static void AddMetadata(ProcessStartInfo start, Sample sample)
    {
        void Meta(string key, string value)
        {
            start.ArgumentList.Add("-metadata");
            start.ArgumentList.Add($"{key}={value}");
        }

        Meta("title", sample.Title);
        Meta("artist", sample.Artist);
        Meta("album", sample.Album);
        Meta("genre", sample.Genre);
        Meta("date", sample.Year);
    }

    private static async Task RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Length > 500 ? error[^500..] : error);
    }

    private static Font Font(float size, FontStyle style)
    {
        if (SystemFonts.TryGet("DejaVu Sans", out var family))
            return family.CreateFont(size, style);
        return SystemFonts.CreateFont(SystemFonts.Families.First().Name, size, style);
    }
}
