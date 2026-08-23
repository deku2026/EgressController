using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EgressController.Core.Ipc;

public enum ElevatedIpcKind
{
    Hello = 1,
    Start = 2,
    Restart = 3,
    Stop = 4,
    GetStatus = 5,
    Shutdown = 6,
    Response = 100,
    StatusEvent = 101,
    OutputEvent = 102,
}

/// <summary>
/// The only wire message accepted by ElevatedHost. Optional fields are intentionally explicit;
/// no arbitrary executable, argument list or JSON extension data is part of this protocol.
/// </summary>
public sealed record ElevatedIpcMessage
{
    public required int Version { get; init; }
    public required ElevatedIpcKind Kind { get; init; }
    public required string RequestId { get; init; }
    public int ClientProcessId { get; init; }
    public string? CorePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? CoreSha256 { get; init; }
    public string? ConfigSha256 { get; init; }
    public int? ProcessId { get; init; }
    public string? State { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? OutputSource { get; init; }
    public string? OutputLine { get; init; }
    public int DroppedOutputCount { get; init; }

    public static ElevatedIpcMessage Request(ElevatedIpcKind kind, int clientProcessId)
        => new()
        {
            Version = ElevatedIpcProtocol.CurrentVersion,
            Kind = kind,
            RequestId = Guid.NewGuid().ToString("N"),
            ClientProcessId = clientProcessId,
        };

    public ElevatedIpcMessage AsResponse(bool succeeded, string? errorCode = null, string? errorMessage = null)
        => this with
        {
            Version = ElevatedIpcProtocol.CurrentVersion,
            Kind = ElevatedIpcKind.Response,
            ErrorCode = succeeded ? null : errorCode,
            ErrorMessage = succeeded ? null : errorMessage,
        };
}

public static class ElevatedIpcProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxFrameBytes = 1024 * 1024;

    public static byte[] Serialize(ElevatedIpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, ElevatedIpcJsonContext.Default.ElevatedIpcMessage);
    }

    public static async Task WriteAsync(
        Stream stream,
        ElevatedIpcMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = Serialize(message);
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException("IPC message exceeds the maximum frame size.");
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ElevatedIpcMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[sizeof(int)];
        int prefixRead = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixRead == 0)
            return null;
        if (prefixRead != prefix.Length)
            throw new EndOfStreamException("IPC frame length was truncated.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 2 or > MaxFrameBytes)
            throw new InvalidDataException("IPC frame length is outside the allowed range.");
        byte[] payload = new byte[length];
        int payloadRead = await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead != length)
            throw new EndOfStreamException("IPC frame payload was truncated.");

        ElevatedIpcMessage message = JsonSerializer.Deserialize(
            payload,
            ElevatedIpcJsonContext.Default.ElevatedIpcMessage)
            ?? throw new InvalidDataException("IPC frame contained null JSON.");
        Validate(message);
        return message;
    }

    public static void Validate(ElevatedIpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported IPC protocol version: {message.Version}.");
        if (!Guid.TryParseExact(message.RequestId, "N", out _))
            throw new InvalidDataException("IPC request-id must be a compact GUID.");
        if (!Enum.IsDefined(message.Kind))
            throw new InvalidDataException("IPC message kind is unknown.");
        if (message.Kind is >= ElevatedIpcKind.Hello and <= ElevatedIpcKind.Shutdown
            && message.ClientProcessId <= 0)
            throw new InvalidDataException("IPC command is missing a client process id.");
        if (message.Kind is ElevatedIpcKind.Start or ElevatedIpcKind.Restart)
        {
            RequireSha(message.CoreSha256, "core");
            RequireSha(message.ConfigSha256, "config");
            if (string.IsNullOrWhiteSpace(message.CorePath) || string.IsNullOrWhiteSpace(message.ConfigPath))
                throw new InvalidDataException("IPC start command is missing core/config paths.");
        }
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            offset += read;
        }
        return offset;
    }

    private static void RequireSha(string? value, string label)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"IPC {label} SHA-256 is invalid.");
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ElevatedIpcMessage))]
internal sealed partial class ElevatedIpcJsonContext : JsonSerializerContext;
