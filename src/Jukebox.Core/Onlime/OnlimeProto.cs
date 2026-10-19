using System.Buffers.Binary;
using System.Text;

namespace Jukebox.Core.Onlime;

public static class OnlimeProto
{
    public readonly record struct Field(int Number, int Wire, ulong Varint, byte[] Bytes);

    public static byte[] LookupRequest(string shareId)
    {
        var buffer = new List<byte>();
        WriteString(buffer, 1, shareId);
        return buffer.ToArray();
    }

    public static byte[] GetPathRequest(string path, string? cursor)
    {
        var buffer = new List<byte>();
        WriteString(buffer, 1, path);
        WriteVarintField(buffer, 2, 1);
        WriteVarintField(buffer, 3, 200);
        WriteVarintField(buffer, 4, 1);
        if (!string.IsNullOrEmpty(cursor))
            WriteString(buffer, 6, cursor);
        return buffer.ToArray();
    }

    public static byte[] Frame(byte[] message)
    {
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), message.Length);
        message.CopyTo(frame.AsSpan(5));
        return frame;
    }

    public static List<Field> Read(ReadOnlySpan<byte> data)
    {
        var fields = new List<Field>();
        var index = 0;
        while (index < data.Length)
        {
            var (key, used) = ReadVarint(data, index);
            index += used;
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (number <= 0)
                throw new InvalidDataException("Bad protobuf field.");
            switch (wire)
            {
                case 0:
                    var (value, width) = ReadVarint(data, index);
                    index += width;
                    fields.Add(new Field(number, 0, value, []));
                    break;
                case 1:
                    index += 8;
                    break;
                case 2:
                    var (length, lengthWidth) = ReadVarint(data, index);
                    index += lengthWidth;
                    var size = (int)length;
                    if (size < 0 || index + size > data.Length)
                        throw new InvalidDataException("Bad protobuf length.");
                    fields.Add(new Field(number, 2, 0, data.Slice(index, size).ToArray()));
                    index += size;
                    break;
                case 5:
                    index += 4;
                    break;
                default:
                    throw new InvalidDataException("Bad protobuf wire type.");
            }
        }

        return fields;
    }

    public static (byte[]? Message, int Status, string GrpcMessage) OpenFrame(byte[] body)
    {
        byte[]? message = null;
        var status = 0;
        var grpcMessage = "";
        var sawStatus = false;
        var index = 0;
        while (index + 5 <= body.Length)
        {
            var flag = body[index];
            var length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(index + 1));
            index += 5;
            if (length < 0 || index + length > body.Length)
                throw new InvalidDataException("Bad grpc-web frame.");
            var payload = body.AsSpan(index, length);
            index += length;
            if ((flag & 0x80) != 0)
            {
                var text = Encoding.ASCII.GetString(payload);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var split = line.IndexOf(':');
                    if (split <= 0)
                        continue;
                    var name = line[..split].Trim();
                    var value = line[(split + 1)..].Trim();
                    if (name.Equals("grpc-status", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var code))
                    {
                        status = code;
                        sawStatus = true;
                    }
                    else if (name.Equals("grpc-message", StringComparison.OrdinalIgnoreCase))
                    {
                        grpcMessage = Uri.UnescapeDataString(value);
                    }
                }
            }
            else if (message is null)
            {
                message = payload.ToArray();
            }
        }

        if (!sawStatus && message is null && body.Length > 0)
            throw new InvalidDataException("Bad grpc-web body.");
        return (message, status, grpcMessage);
    }

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static void WriteString(List<byte> buffer, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteKey(buffer, field, 2);
        WriteVarint(buffer, (ulong)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void WriteVarintField(List<byte> buffer, int field, ulong value)
    {
        WriteKey(buffer, field, 0);
        WriteVarint(buffer, value);
    }

    private static void WriteKey(List<byte> buffer, int field, int wire) =>
        WriteVarint(buffer, (ulong)((field << 3) | wire));

    private static void WriteVarint(List<byte> buffer, ulong value)
    {
        while (value > 0x7F)
        {
            buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }

    private static (ulong Value, int Used) ReadVarint(ReadOnlySpan<byte> data, int index)
    {
        ulong value = 0;
        var shift = 0;
        var used = 0;
        while (index + used < data.Length && used < 10)
        {
            var byteValue = data[index + used];
            used++;
            value |= (ulong)(byteValue & 0x7F) << shift;
            if ((byteValue & 0x80) == 0)
                return (value, used);
            shift += 7;
        }

        throw new InvalidDataException("Bad protobuf varint.");
    }
}
