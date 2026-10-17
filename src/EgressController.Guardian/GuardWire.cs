using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EgressController.Guardian;

/// <summary>Bounded, length-prefixed, source-generated JSON; no reflection or HTTP host.</summary>
public static class GuardWire
{
    public const int MaximumFrame = 4 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> type, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, type);
        if (payload.Length > MaximumFrame) throw new InvalidDataException("守护消息超过大小限制。");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is <= 0 or > MaximumFrame) throw new InvalidDataException("守护消息长度无效。");
        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, type) ?? throw new InvalidDataException("守护消息为空。");
    }
}
