using System.Net;
using System.Text;
using Jukebox.Core.Onlime;

namespace Jukebox.Tests;

public class OnlimeShareTests
{
    [Fact]
    public void Reads_a_public_onlime_folder_link()
    {
        Assert.True(OnlimeShareLink.TryParse("https://cloud.onlime.dk/s/30283228a54170a4473b29e0fe909f838de", out var link));
        Assert.Equal("30283228a54170a4473b29e0fe909f838de", link.ShareId);
        Assert.Equal("/", link.FolderPath);
        Assert.Equal("https://cloud.onlime.dk/s/30283228a54170a4473b29e0fe909f838de", link.Url);

        Assert.True(OnlimeShareLink.TryParse("https://www.jottacloud.com/s/abcdef012345/list/Jul", out var nested));
        Assert.Equal("/Jul", nested.FolderPath);

        Assert.False(OnlimeShareLink.TryParse("https://example.com/s/abcdef012345", out _));
        Assert.False(OnlimeShareLink.TryParse("https://cloud.onlime.dk/web/secure", out _));
    }

    [Fact]
    public async Task A_disabled_share_is_explained_in_danish()
    {
        var body = new byte[] { 0, 0, 0, 0, 4, 0x1a, 0x02, 0x08, 0x03 };
        body = [..body, 0x80, 0, 0, 0, 15, ..Encoding.ASCII.GetBytes("grpc-status:0\r\n")];
        using var client = new OnlimeShareClient(new ScriptHandler(_ => body));
        var report = await client.FetchAsync(Parsed("https://cloud.onlime.dk/s/abcdef012345"), Directory.CreateTempSubdirectory().FullName, [], CancellationToken.None);
        Assert.Equal(0, report.Added);
        Assert.Contains("slået fra", report.Message);
    }

    [Fact]
    public async Task New_songs_are_fetched_and_known_names_stay()
    {
        var root = Wire.Frame(Wire.Msg(1, Wire.Join(
            Wire.Str(1, "/"),
            Wire.Num(4, 2),
            Wire.Msg(13, Wire.File("/Old.mp4", "Old.mp4", "https://download.jottacloud.com/old", 4)),
            Wire.Msg(13, Wire.File("/New.mp4", "New.mp4", "https://download.jottacloud.com/new", 3)),
            Wire.Msg(13, Wire.File("/Queen.mp4", "Queen.mp4", "https://download.jottacloud.com/queen", 3)),
            Wire.Msg(13, Wire.Join(Wire.Str(1, "/Sub"), Wire.Str(2, "Sub"), Wire.Num(4, 2))),
            Wire.Msg(13, Wire.File("/Cover.jpg", "Cover.jpg", "https://download.jottacloud.com/cover.jpg", 2)))));
        var sub = Wire.Frame(Wire.Msg(1, Wire.Join(
            Wire.Str(1, "/Sub"),
            Wire.Num(4, 2),
            Wire.Msg(13, Wire.File("/Sub/Extra.mp3", "Extra.mp3", "https://download.jottacloud.com/extra", 2)))));
        var lookup = Wire.Frame(Wire.Msg(2, Wire.Msg(1, Wire.Str(1, "tok"))));
        var handler = new ShareHandler(lookup, root, sub);
        var directory = Directory.CreateTempSubdirectory("onlime-share-").FullName;
        File.WriteAllBytes(Path.Combine(directory, "Old.mp4"), [1, 2, 3, 4]);
        using var client = new OnlimeShareClient(handler);
        var link = Parsed("https://cloud.onlime.dk/s/abcdef012345");

        var report = await client.FetchAsync(link, directory, ["Queen.mp4"], CancellationToken.None, freeSpace: () => 5_000_000_000);

        Assert.Equal(2, report.Added);
        Assert.Equal(2, report.Skipped);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(directory, "Old.mp4")));
        Assert.Equal(new byte[] { 5, 5, 5 }, File.ReadAllBytes(Path.Combine(directory, "New.mp4")));
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(Path.Combine(directory, "Extra.mp3")));
        Assert.False(File.Exists(Path.Combine(directory, "Queen.mp4")));
        Assert.False(File.Exists(Path.Combine(directory, "Cover.jpg")));
        Assert.DoesNotContain(handler.Downloads, url => url.Contains("/old", StringComparison.Ordinal) || url.Contains("/queen", StringComparison.Ordinal) || url.Contains("cover", StringComparison.Ordinal));
        Assert.Contains(handler.Downloads, url => url.Contains("/new", StringComparison.Ordinal) && url.Contains("access_token=tok", StringComparison.Ordinal));
        Assert.Contains(handler.Downloads, url => url.Contains("/extra", StringComparison.Ordinal) && url.Contains("access_token=tok", StringComparison.Ordinal));
        Assert.Contains("2 sange er hentet", report.Message);
        Assert.Contains("allerede", report.Message);
    }

    [Fact]
    public async Task A_full_disk_stops_before_the_download()
    {
        var lookup = Wire.Frame(Wire.Msg(2, Wire.Msg(1, Wire.Str(1, "tok"))));
        var root = Wire.Frame(Wire.Msg(1, Wire.Join(
            Wire.Str(1, "/"),
            Wire.Num(4, 2),
            Wire.Msg(13, Wire.File("/New.mp4", "New.mp4", "https://download.jottacloud.com/new", 3)))));
        var handler = new ShareHandler(lookup, root, root);
        using var client = new OnlimeShareClient(handler);
        var directory = Directory.CreateTempSubdirectory("onlime-full-").FullName;
        var report = await client.FetchAsync(Parsed("https://cloud.onlime.dk/s/abcdef012345"), directory, [], CancellationToken.None, freeSpace: () => 0);
        Assert.Equal(0, report.Added);
        Assert.True(report.DiskFull);
        Assert.Empty(handler.Downloads);
        Assert.Contains("Disken er fuld", report.Message);
    }

    private static OnlimeShareLink Parsed(string url)
    {
        Assert.True(OnlimeShareLink.TryParse(url, out var link));
        return link;
    }

    private static class Wire
    {
        public static byte[] Frame(byte[] message) => OnlimeProto.Frame(message);

        public static byte[] File(string path, string name, string url, ulong size) =>
            Join(Str(1, path), Str(2, name), Num(4, 1), Str(6, url), Num(7, size));

        public static byte[] Join(params byte[][] parts)
        {
            var all = new byte[parts.Sum(part => part.Length)];
            var index = 0;
            foreach (var part in parts)
            {
                part.CopyTo(all, index);
                index += part.Length;
            }

            return all;
        }

        public static byte[] Str(int field, string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            return Join(Key(field, 2), Var((ulong)data.Length), data);
        }

        public static byte[] Num(int field, ulong value) => Join(Key(field, 0), Var(value));

        public static byte[] Msg(int field, byte[] inner) => Join(Key(field, 2), Var((ulong)inner.Length), inner);

        private static byte[] Key(int field, int wire) => Var((ulong)((field << 3) | wire));

        private static byte[] Var(ulong value)
        {
            var bytes = new List<byte>();
            while (value > 0x7F)
            {
                bytes.Add((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            bytes.Add((byte)value);
            return bytes.ToArray();
        }
    }

    private sealed class ScriptHandler(Func<HttpRequestMessage, byte[]> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body(request))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc-web+proto");
            return Task.FromResult(response);
        }
    }

    private sealed class ShareHandler(byte[] lookup, byte[] root, byte[] sub) : HttpMessageHandler
    {
        public List<string> Downloads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("LookupShare", StringComparison.Ordinal))
                return Proto(lookup);
            if (path.EndsWith("GetPath", StringComparison.Ordinal))
            {
                if (request.Headers.Authorization?.Parameter != "tok")
                    throw new InvalidOperationException("Missing share token.");
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var folder = "/";
                foreach (var field in OnlimeProto.Read(bytes.AsSpan(5)))
                {
                    if (field.Number == 1 && field.Wire == 2)
                        folder = OnlimeProto.Text(field.Bytes);
                }

                return Proto(folder == "/Sub" ? sub : root);
            }

            Downloads.Add(request.RequestUri!.ToString());
            var payload = path.Contains("/extra", StringComparison.Ordinal) ? new byte[] { 7, 7 } : new byte[] { 5, 5, 5 };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }

        private static HttpResponseMessage Proto(byte[] body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc-web+proto");
            return response;
        }
    }
}
