// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text.Json;

namespace EgressController.Mac;

/// <summary>One bounded request per frame: a four-byte network-order length followed by UTF-8 JSON.</summary>
public static class MacHelperFrameCodec
{
    public static byte[] Encode(MacHelperRequest request)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, MacHelperJson.Default.MacHelperRequest);
        if (payload.Length > MacHelperRequestValidator.MaximumFrameBytes) throw new InvalidDataException("Helper request exceeds the frame limit.");
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public static MacHelperRequest DecodeRequest(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) throw new InvalidDataException("Incomplete helper frame header.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(frame);
        if (length == 0 || length > MacHelperRequestValidator.MaximumFrameBytes || frame.Length - 4 != length)
            throw new InvalidDataException("Incomplete, oversized or trailing helper frame data.");
        return JsonSerializer.Deserialize(frame[4..], MacHelperJson.Default.MacHelperRequest)
            ?? throw new InvalidDataException("Empty helper request.");
    }
}
