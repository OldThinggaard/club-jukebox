using System.Diagnostics;
using System.Globalization;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Jukebox.Core.Metadata;

namespace Jukebox.Core.Thumbnails;

public sealed class ThumbnailGenerator
{
    private readonly string _directory;

    public ThumbnailGenerator(JukeboxPaths paths) => _directory = paths.Thumbnails;

    public async Task<string> CreateAsync(string sourcePath, string mediaType, ProbeTags? probe, string title, string artist, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".jpg");
        if (mediaType == "image")
        {
            SaveImage(sourcePath, destination);
            return destination;
        }

        if (mediaType == "video" && probe?.DurationMs is > 0)
        {
            var best = await BestVideoFrameAsync(sourcePath, probe.DurationMs.Value / 1000.0, cancellationToken);
            if (best is not null)
            {
                File.Move(best, destination, true);
                return destination;
            }
        }

        if (mediaType == "audio")
        {
            var cover = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
            if (await TryExtractCoverAsync(sourcePath, cover, cancellationToken))
            {
                SaveImage(cover, destination);
                File.Delete(cover);
                return destination;
            }
        }

        DrawPlate(destination, title, artist, mediaType);
        return destination;
    }

    private async Task<string?> BestVideoFrameAsync(string source, double durationSeconds, CancellationToken cancellationToken)
    {
        var ratios = new[] { 0.10, 0.20, 0.40, 0.60, 0.80 };
        string? bestPath = null;
        var bestScore = double.NegativeInfinity;
        foreach (var ratio in ratios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seconds = Math.Max(0.1, durationSeconds * ratio);
            var frame = Path.Combine(Path.GetTempPath(), "jb-frame-" + Guid.NewGuid().ToString("N") + ".jpg");
            var ok = await ExtractFrameAsync(source, seconds, frame, cancellationToken);
            if (!ok || !File.Exists(frame))
                continue;
            try
            {
                using var image = Image.Load<Rgba32>(frame);
                var score = ThumbnailScorer.Score(image).Total;
                if (score > bestScore)
                {
                    if (bestPath is not null)
                        File.Delete(bestPath);
                    bestScore = score;
                    bestPath = frame;
                }
                else
                {
                    File.Delete(frame);
                }
            }
            catch
            {
                File.Delete(frame);
            }
        }

        return bestPath;
    }

    private static async Task<bool> ExtractFrameAsync(string source, double seconds, string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-ss");
        start.ArgumentList.Add(seconds.ToString("0.###", CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add("-frames:v");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-vf");
        start.ArgumentList.Add("scale=960:-2");
        start.ArgumentList.Add("-q:v");
        start.ArgumentList.Add("3");
        start.ArgumentList.Add(destination);
        return await RunAsync(start, cancellationToken) && File.Exists(destination);
    }

    private static async Task<bool> TryExtractCoverAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add("-an");
        start.ArgumentList.Add("-vcodec");
        start.ArgumentList.Add("mjpeg");
        start.ArgumentList.Add("-frames:v");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add(destination);
        return await RunAsync(start, cancellationToken) && File.Exists(destination);
    }

    private static async Task<bool> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return false;
            await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveImage(string source, string destination)
    {
        using var image = Image.Load(source);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(960, 960)
        }));
        image.SaveAsJpeg(destination);
    }

    public static void DrawPlate(string destination, string title, string artist, string mediaType)
    {
        using var image = new Image<Rgba32>(960, 540, Color.ParseHex("#1A1714"));
        var titleFont = FontOrDefault(64, FontStyle.Bold);
        var artistFont = FontOrDefault(32, FontStyle.Regular);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("#E2B15A"), new RectangleF(0, 0, 18, 540));
            ctx.DrawText(Trim(title, 42), titleFont, Color.ParseHex("#F4EFE6"), new PointF(56, 180));
            ctx.DrawText(Trim(string.IsNullOrWhiteSpace(artist) ? mediaType : artist, 48), artistFont, Color.ParseHex("#E2B15A"), new PointF(56, 280));
        });
        image.SaveAsJpeg(destination);
    }

    private static Font FontOrDefault(float size, FontStyle style)
    {
        if (SystemFonts.TryGet("DejaVu Sans", out var family))
            return family.CreateFont(size, style);
        return SystemFonts.CreateFont(SystemFonts.Families.First().Name, size, style);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
