using System.Buffers.Binary;
using System.Text.Json;

namespace PocketMic.Core;

public enum PacketType : byte { Hello = 1, Accept = 2, Audio = 3, Ping = 4, Pong = 5, Stop = 6, Error = 7 }
public sealed record Packet(PacketType Type, uint Sequence, byte[] Payload);
public sealed record Hello(string Token, string DeviceName, int SampleRate, int Channels, int Bits, int FrameSamples);

/// <summary>16-byte network-order header; PCM payload is signed little-endian.</summary>
public static class Protocol
{
    public const int SampleRate = 48000, FrameSamples = 960, AudioBytes = FrameSamples * 2;
    public const int HeaderSize = 16, MaxPayload = 8192;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async ValueTask<Packet?> ReadAsync(Stream stream, CancellationToken cancel = default)
    {
        var header = new byte[HeaderSize];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancel) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancel);
        if (!header.AsSpan(0, 4).SequenceEqual("PMIC"u8) || header[4] != 1 || header[6] != 0 || header[7] != 0)
            throw new InvalidDataException("Unsupported PocketMic header.");
        var type = (PacketType)header[5];
        if (!Enum.IsDefined(type)) throw new InvalidDataException("Unknown packet type.");
        var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        if (size > MaxPayload || (type == PacketType.Audio && size != AudioBytes))
            throw new InvalidDataException("Invalid packet length.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancel);
        return new Packet(type, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)), payload);
    }

    // One writer per connection; callers serialize writes when needed.
    public static async ValueTask WriteAsync(Stream stream, PacketType type, byte[] payload, uint sequence = 0, CancellationToken cancel = default)
    {
        if (payload.Length > MaxPayload) throw new ArgumentOutOfRangeException(nameof(payload));
        var bytes = new byte[HeaderSize + payload.Length];
        "PMIC"u8.CopyTo(bytes);
        bytes[4] = 1;
        bytes[5] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(bytes, HeaderSize);
        await stream.WriteAsync(bytes, cancel);
    }

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static bool ValidFormat(Hello hello) => hello.SampleRate == SampleRate && hello.Channels == 1 && hello.Bits == 16 && hello.FrameSamples == FrameSamples;
}
