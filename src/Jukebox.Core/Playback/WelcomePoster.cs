using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jukebox.Core.Playback;

public static class WelcomePoster
{
    public static void Write(string path, string? clubName, string? logoPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var name = string.IsNullOrWhiteSpace(clubName) ? "Videojukebox" : clubName.Trim();
            using var image = new Image<Rgba32>(1920, 1080, Color.ParseHex("#1A140C"));
            image.Mutate(ctx =>
            {
                if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
                {
                    using var logo = Image.Load<Rgba32>(logoPath);
                    logo.Mutate(op => op.Resize(220, 220));
                    ctx.DrawImage(logo, new Point(160, 140), 1f);
                }

                ctx.DrawText(name, Font(name.Length > 24 ? 64 : 92), Color.ParseHex("#F4EFE6"), new PointF(160, 420));
                ctx.Fill(Color.ParseHex("#E2B15A"), new RectangleF(160, 620, 720, 4));
                ctx.DrawText("Videojukebox", Font(36), Color.ParseHex("#E2B15A"), new PointF(160, 660));
            });
            var temp = path + ".tmp";
            image.SaveAsJpeg(temp);
            File.Move(temp, path, true);
        }
        catch (Exception)
        {
            // A missing picture must not stop the jukebox. The television keeps the last one.
        }
    }

    private static Font Font(float size)
    {
        if (SystemFonts.TryGet("DejaVu Sans", out var family))
            return family.CreateFont(size, FontStyle.Bold);
        return SystemFonts.CreateFont(SystemFonts.Families.First().Name, size, FontStyle.Bold);
    }
}
