// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text.Json;

namespace EgressController.Mac;

public static class MacHelperWireCodec
{
    public static byte[] EncodeRequest(MacHelperWireRequest value) => Frame(JsonSerializer.SerializeToUtf8Bytes(value, MacHelperWireJson.Default.MacHelperWireRequest));
    public static byte[] EncodeReply(MacHelperWireReply value) => Frame(JsonSerializer.SerializeToUtf8Bytes(value, MacHelperWireJson.Default.MacHelperWireReply));
    public static MacHelperWireRequest DecodeRequest(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize(Payload(bytes), MacHelperWireJson.Default.MacHelperWireRequest) ?? throw new InvalidDataException("Empty helper request.");
    public static MacHelperWireReply DecodeReply(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize(Payload(bytes), MacHelperWireJson.Default.MacHelperWireReply) ?? throw new InvalidDataException("Empty helper reply.");
    public static async Task<MacHelperWireRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default)
        => DecodeRequest(await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false));
    public static async Task<MacHelperWireReply> ReadReplyAsync(Stream stream, CancellationToken cancellationToken = default)
        => DecodeReply(await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false));
    public static Task WriteRequestAsync(Stream stream, MacHelperWireRequest value, CancellationToken cancellationToken = default)
        => stream.WriteAsync(EncodeRequest(value), cancellationToken).AsTask();
    public static Task WriteReplyAsync(Stream stream, MacHelperWireReply value, CancellationToken cancellationToken = default)
        => stream.WriteAsync(EncodeReply(value), cancellationToken).AsTask();
    private static byte[] Frame(byte[] payload)
    {
        if (payload.Length is 0 or > MacHelperRequestValidator.MaximumFrameBytes) throw new InvalidDataException("Helper frame limit exceeded.");
        byte[] bytes = new byte[payload.Length + 4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length); payload.CopyTo(bytes, 4); return bytes;
    }
    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new InvalidDataException("Missing helper frame header.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (length is 0 or > MacHelperRequestValidator.MaximumFrameBytes || bytes.Length - 4 != length) throw new InvalidDataException("Invalid helper frame length.");
        return bytes[4..];
    }
    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MacHelperRequestValidator.MaximumFrameBytes) throw new InvalidDataException("Invalid helper frame length.");
        byte[] bytes = new byte[length + 4]; header.CopyTo(bytes, 0);
        await stream.ReadExactlyAsync(bytes.AsMemory(4), cancellationToken).ConfigureAwait(false); return bytes;
    }
}
