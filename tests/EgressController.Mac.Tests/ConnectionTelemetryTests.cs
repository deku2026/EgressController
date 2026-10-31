// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.Mac.Tests;

public sealed class ConnectionTelemetryTests
{
    [Fact]
    public async Task InvalidOptionalTelemetryNeverInvalidatesHealthyStatusOrClosesTheControlConnection()
    {
        var connection = new Connection { Snapshot = new(null!, DateTimeOffset.UtcNow) };
        await using var client = new MacHelperClient(connection);
        var status = await client.InspectAsync(TestContext.Current.CancellationToken);
        Assert.True(status.TunTakenOver); Assert.True(client.Status.Running); Assert.False(client.ConnectionSnapshot.IsAvailable);
        Assert.Equal(0, connection.Disposals);
        connection.Snapshot = new([Row("valid")], DateTimeOffset.UtcNow);
        await client.InspectAsync(TestContext.Current.CancellationToken);
        Assert.True(client.ConnectionSnapshot.IsAvailable); Assert.Single(client.ConnectionSnapshot.Records);
        Assert.True(client.Status.TunTakenOver); Assert.Equal(0, connection.Disposals);
    }

    [Fact]
    public async Task ClientCacheDetachesAdapterOwnedCollectionsAndDoesNotIssueObservationRequests()
    {
        var rows = new List<CoreConnectionRecord> { Row("valid") };
        var connection = new Connection { Snapshot = new(rows, DateTimeOffset.UtcNow) };
        await using var client = new MacHelperClient(connection);
        await client.InspectAsync(TestContext.Current.CancellationToken); rows.Clear();
        Assert.Single(client.ConnectionSnapshot.Records);
        Assert.Throws<NotSupportedException>(() => ((IList<CoreConnectionRecord>)client.ConnectionSnapshot.Records).Clear());
        Assert.Equal(1, connection.Requests);
    }

    [Fact]
    public async Task TelemetryAdapterEnumerationFailureIsContainedOutsideTheStatusFailurePath()
    {
        var connection = new Connection { Snapshot = new(new FailingRows(), DateTimeOffset.UtcNow) };
        await using var client = new MacHelperClient(connection);
        Assert.True((await client.InspectAsync(TestContext.Current.CancellationToken)).TunTakenOver);
        Assert.False(client.ConnectionSnapshot.IsAvailable); Assert.Equal(0, connection.Disposals);
    }

    [Theory]
    [InlineData("records-type")]
    [InlineData("timestamp-type")]
    [InlineData("row-type")]
    [InlineData("unknown-field")]
    public void MalformedOptionalJsonPayloadIsDiscardedWhileAuthenticatedStatusStillDecodes(string tamper)
    {
        var reply = Reply(Guid.NewGuid(), 1, new([Row("valid")], DateTimeOffset.UtcNow));
        var json = JsonNode.Parse(JsonSerializer.Serialize(reply, MacHelperWireJson.Default.MacHelperWireReply))!;
        switch (tamper)
        {
            case "records-type": json["Connections"]!["Records"] = "invalid"; break;
            case "timestamp-type": json["Connections"]!["CapturedAt"] = new JsonObject(); break;
            case "row-type": json["Connections"]!["Records"] = new JsonArray(7); break;
            case "unknown-field": json["Connections"]!["SecretRawLog"] = "must be discarded"; break;
        }
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        byte[] frame = new byte[payload.Length + 4]; BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length); payload.CopyTo(frame, 4);
        var decoded = MacHelperWireCodec.DecodeReply(frame);
        Assert.True(decoded.Status.TunTakenOver); Assert.Null(decoded.Connections);
    }

    [Fact]
    public void WorstAcceptedEscapedObservationPayloadRemainsWithinTheBoundedWireFrame()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 128).Select(index => new CoreConnectionRecord("row-" + index, now)
        { ExecutablePath = "/" + new string('界', 650), Host = "example.test", OutboundTag = "node-1", Transport = "tcp", RemotePort = 443 }).ToArray();
        var snapshot = new CoreConnectionSnapshot(rows, now, Limitation: new string('界', 256));
        Assert.True(CoreConnectionSnapshots.IsValid(snapshot, now));
        byte[] frame = MacHelperWireCodec.EncodeReply(Reply(Guid.NewGuid(), 1, snapshot));
        Assert.True(frame.Length <= MacHelperRequestValidator.MaximumFrameBytes + 4);
        Assert.Equal(128, MacHelperWireCodec.DecodeReply(frame).Connections!.Records.Count);
    }

    private static CoreConnectionRecord Row(string id) => new(id, DateTimeOffset.UtcNow) { RemoteIp = "203.0.113.1", RemotePort = 443, Transport = "tcp", OutboundTag = "node-1" };
    private static MacHelperWireReply Reply(Guid session, long sequence, CoreConnectionSnapshot? snapshot)
        => new(session, sequence, true, "ok", "", new(true, true, true, false, 1234, "generation", null, DateTimeOffset.UtcNow), snapshot);
    private sealed class Connection : IMacHelperConnection
    {
        public CoreConnectionSnapshot? Snapshot;
        public int Disposals, Requests;
        public Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(Reply(request.SessionId, request.Sequence, Snapshot)); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class FailingRows : IReadOnlyList<CoreConnectionRecord>
    {
        public int Count => throw new InvalidOperationException("Synthetic telemetry error.");
        public CoreConnectionRecord this[int index] => throw new NotSupportedException();
        public IEnumerator<CoreConnectionRecord> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
