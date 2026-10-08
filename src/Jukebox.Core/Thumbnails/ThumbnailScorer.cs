using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jukebox.Core.Thumbnails;

public readonly record struct FrameScore(double LumaMean, double LumaVariance, double ChromaMean, double Total);

public static class ThumbnailScorer
{
    public static FrameScore Score(Image<Rgba32> image)
    {
        double sumL = 0;
        double sumL2 = 0;
        double sumC = 0;
        long count = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y += 4)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += 4)
                {
                    var pixel = row[x];
                    var luma = (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
                    var chroma = (Math.Abs(pixel.R - pixel.G) + Math.Abs(pixel.G - pixel.B) + Math.Abs(pixel.B - pixel.R)) / 3.0;
                    sumL += luma;
                    sumL2 += luma * luma;
                    sumC += chroma;
                    count++;
                }
            }
        });

        if (count == 0)
            return new FrameScore(0, 0, 0, 0);

        var mean = sumL / count;
        var variance = Math.Max(0, (sumL2 / count) - (mean * mean));
        var chromaMean = sumC / count;
        var score = variance + (chromaMean * 0.35);
        if (mean < 18)
            score *= 0.05;
        else if (mean < 35)
            score *= 0.35;
        if (mean > 245)
            score *= 0.15;
        if (variance < 40)
            score *= 0.25;
        return new FrameScore(mean, variance, chromaMean, score);
    }
}
