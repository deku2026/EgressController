using EgressController.Core.Ipc;

namespace EgressController.ElevatedHost.Tests;

public sealed class ElevatedIpcProtocolTests
{
    [Fact]
    public async Task Length_prefixed_message_round_trips_with_fixed_fields()
    {
        ElevatedIpcMessage original = ElevatedIpcMessage.Request(
            ElevatedIpcKind.Start,
            Environment.ProcessId) with
        {
            CorePath = @"C:\data\core\1.13.19\sing-box.exe",
            ConfigPath = @"C:\data\config.next.json",
            CoreSha256 = new('a', 64),
            ConfigSha256 = new('b', 64),
        };
        await using var stream = new MemoryStream();
        await ElevatedIpcProtocol.WriteAsync(stream, original, TestContext.Current.CancellationToken);
        stream.Position = 0;

        ElevatedIpcMessage? roundTrip = await ElevatedIpcProtocol.ReadAsync(
            stream,
            TestContext.Current.CancellationToken);

        Assert.NotNull(roundTrip);
        Assert.Equal(original.RequestId, roundTrip!.RequestId);
        Assert.Equal(ElevatedIpcKind.Start, roundTrip.Kind);
        Assert.Equal(original.CorePath, roundTrip.CorePath);
        Assert.Equal(original.ConfigSha256, roundTrip.ConfigSha256);
    }

    [Fact]
    public void Unknown_version_kind_and_missing_start_hashes_are_rejected()
    {
        ElevatedIpcMessage hello = ElevatedIpcMessage.Request(ElevatedIpcKind.Hello, Environment.ProcessId);
        Assert.Throws<InvalidDataException>(() => ElevatedIpcProtocol.Serialize(hello with { Version = 2 }));
        Assert.Throws<InvalidDataException>(() => ElevatedIpcProtocol.Serialize(hello with { Kind = (ElevatedIpcKind)999 }));

        ElevatedIpcMessage start = ElevatedIpcMessage.Request(ElevatedIpcKind.Start, Environment.ProcessId) with
        {
            CorePath = @"C:\data\sing-box.exe",
            ConfigPath = @"C:\data\config.json",
        };
        Assert.Throws<InvalidDataException>(() => ElevatedIpcProtocol.Serialize(start));
    }

    [Fact]
    public async Task Truncated_frame_and_oversized_frame_are_rejected()
    {
        await using var truncated = new MemoryStream(new byte[] { 8, 0, 0 });
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => ElevatedIpcProtocol.ReadAsync(truncated, TestContext.Current.CancellationToken));

        byte[] oversized = new byte[sizeof(int)];
        BitConverter.GetBytes(ElevatedIpcProtocol.MaxFrameBytes + 1).CopyTo(oversized, 0);
        await using var tooLarge = new MemoryStream(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ElevatedIpcProtocol.ReadAsync(tooLarge, TestContext.Current.CancellationToken));
    }
}
