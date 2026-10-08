using System.Security.Cryptography;

namespace Jukebox.Core.Metadata;

public static class SampleHash
{
    public static string Compute(string path)
    {
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var read = stream.Read(buffer, 0, buffer.Length);
        incremental.AppendData(buffer.AsSpan(0, read));
        if (info.Length > buffer.Length * 2)
        {
            stream.Seek(-buffer.Length, SeekOrigin.End);
            read = stream.Read(buffer, 0, buffer.Length);
            incremental.AppendData(buffer.AsSpan(0, read));
        }

        Span<byte> size = stackalloc byte[8];
        BitConverter.TryWriteBytes(size, info.Length);
        incremental.AppendData(size);
        return Convert.ToHexString(incremental.GetHashAndReset());
    }
}
