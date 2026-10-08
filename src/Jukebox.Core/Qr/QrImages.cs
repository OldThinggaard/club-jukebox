using QRCoder;

namespace Jukebox.Core.Qr;

public static class QrImages
{
    public static string Svg(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(4, "#14120F", "#F4EFE6", true);
    }
}
