using System.Text.Json;
using EgressController.Core.Protection;

namespace EgressController.Guardian.Tests;

public sealed class GuardProtocolTests
{
    private static GuardRequest Hello(Guid id) => new() { Kind = "hello", Session = id, Policy = Fixture.Policy };
    [Fact]
    public void Same_process_name_at_other_path_cannot_impersonate_main()
    {
        var runtime = new GuardRuntime(new FakeStore(), new FakeProcesses(), new FakeFacts(), []);
        using var peer = new GuardRequestHandler(Fixture.Process(10, @"C:\other\controller.exe"), Fixture.Policy.MainExecutable,
            runtime, () => Fixture.Policy, () => { });
        Assert.False(peer.Handle(Hello(Guid.NewGuid())).Success);
        Assert.True(peer.Handle(new() { Kind = "status" }).Success);
    }
    [Fact]
    public void Handshake_commits_policy_before_ack_and_disconnect_revokes_lease()
    {
        var store = new FakeStore(); var runtime = new GuardRuntime(store, new FakeProcesses(), new FakeFacts(), []);
        Guid id = Guid.NewGuid();
        using var peer = new GuardRequestHandler(Fixture.Process(10, Fixture.Policy.MainExecutable), Fixture.Policy.MainExecutable, runtime, () => Fixture.Policy, () => { });
        Assert.True(peer.Handle(Hello(id)).Success);
        Assert.True(peer.Handle(new() { Kind = "heartbeat", Session = id, Sequence = 1, Heartbeat = Fixture.Heartbeat() }).Success);
        Assert.False(runtime.Tick().Protecting);
        peer.Dispose();
        Assert.True(runtime.Tick().Protecting);
    }
    [Fact]
    public void Failed_commit_is_not_acknowledged()
    {
        var store = new FakeStore { Policy = null, PolicyWriteFails = true };
        var runtime = new GuardRuntime(store, new FakeProcesses(), new FakeFacts(), []);
        using var peer = new GuardRequestHandler(Fixture.Process(10, Fixture.Policy.MainExecutable), Fixture.Policy.MainExecutable, runtime, () => Fixture.Policy, () => { });
        Assert.False(peer.Handle(Hello(Guid.NewGuid())).Success);
        Assert.Null(store.Policy);
        Assert.True(runtime.Tick().Protecting);
    }
    [Fact]
    public void Old_connection_cannot_change_new_session_policy_or_prepare_transition()
    {
        var runtime = new GuardRuntime(new FakeStore(), new FakeProcesses(), new FakeFacts(), []);
        using var old = new GuardRequestHandler(Fixture.Process(10, Fixture.Policy.MainExecutable), Fixture.Policy.MainExecutable, runtime, () => Fixture.Policy, () => { });
        using var current = new GuardRequestHandler(Fixture.Process(11, Fixture.Policy.MainExecutable), Fixture.Policy.MainExecutable, runtime, () => Fixture.Policy, () => { });
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        old.Handle(Hello(first)); current.Handle(Hello(second));
        current.Handle(new() { Kind = "heartbeat", Session = second, Sequence = 1, Heartbeat = Fixture.Heartbeat() });
        Assert.False(old.Handle(new() { Kind = "policy", Session = first, Sequence = 100, Policy = Fixture.Policy }).Success);
        Assert.False(old.Handle(new() { Kind = "prepare", Session = first, Sequence = 101 }).Success);
        old.Dispose();
        Assert.False(runtime.Tick().Protecting);
    }
    [Fact]
    public void Late_healthy_frame_cannot_undo_prepare()
    {
        var runtime = new GuardRuntime(new FakeStore(), new FakeProcesses(), new FakeFacts(), []);
        using var peer = new GuardRequestHandler(Fixture.Process(10, Fixture.Policy.MainExecutable), Fixture.Policy.MainExecutable, runtime, () => Fixture.Policy, () => { });
        Guid id = Guid.NewGuid(); peer.Handle(Hello(id));
        peer.Handle(new() { Kind = "heartbeat", Session = id, Sequence = 1, Heartbeat = Fixture.Heartbeat() });
        Assert.True(peer.Handle(new() { Kind = "prepare", Session = id, Sequence = 3 }).Success);
        Assert.False(peer.Handle(new() { Kind = "heartbeat", Session = id, Sequence = 2, Heartbeat = Fixture.Heartbeat() }).Success);
        Assert.True(runtime.Tick().Protecting);
    }
    [Fact]
    public void Older_mailbox_policy_and_same_revision_with_changed_contents_are_rejected()
    {
        var runtime = new GuardRuntime(new FakeStore(), new FakeProcesses(), new FakeFacts(), []);
        var latest = Fixture.Policy with { Revision = Guid.NewGuid().ToString("N") };
        using var peer = new GuardRequestHandler(Fixture.Process(10, Fixture.Policy.MainExecutable), Fixture.Policy.MainExecutable, runtime, () => latest, () => { });
        Assert.False(peer.Handle(Hello(Guid.NewGuid())).Success);
        Assert.Throws<InvalidDataException>(() => runtime.SetPolicy(Fixture.Policy with { Executables = [] }));
    }
    [Theory]
    [InlineData("kill-any-pid", GuardRequest.CurrentProtocol)]
    [InlineData("heartbeat", GuardRequest.CurrentProtocol)]
    [InlineData("status", 1)]
    [InlineData("status", 99)]
    public void Unsupported_or_unhandshaken_requests_do_not_release_protection(string kind, int version)
    {
        var runtime = new GuardRuntime(new FakeStore(), new FakeProcesses(), new FakeFacts(), []);
        using var peer = new GuardRequestHandler(Fixture.Process(10, Fixture.Policy.MainExecutable), Fixture.Policy.MainExecutable, runtime, () => Fixture.Policy, () => { });
        Assert.False(peer.Handle(new() { Kind = kind, Protocol = version, Session = Guid.NewGuid(), Sequence = 1, Heartbeat = Fixture.Heartbeat() }).Success);
        Assert.True(runtime.Tick().Protecting);
    }
    [Fact]
    public void Aot_protocol_roundtrips_utf8_and_has_no_network_health_field()
    {
        var policy = Fixture.Policy with { Executables = [new(@"C:\应用\程序.exe", "中文应用")] };
        var request = new GuardRequest { Kind = "policy", Policy = policy };
        string json = JsonSerializer.Serialize(request, GuardJson.Default.GuardRequest);
        var restored = JsonSerializer.Deserialize(json, GuardJson.Default.GuardRequest)!;
        Assert.Equal(policy.Executables[0], restored.Policy!.Executables[0]);
        string heartbeat = JsonSerializer.Serialize(new GuardRequest { Heartbeat = Fixture.Heartbeat() }, GuardJson.Default.GuardRequest);
        Assert.DoesNotContain("Network", heartbeat);
        Assert.DoesNotContain("Internet", heartbeat);
        Assert.DoesNotContain("Doh", heartbeat);
    }
    [Fact]
    public async Task Framing_handles_split_reads_and_multiple_messages()
    {
        using var stream = new MemoryStream();
        await GuardWire.WriteAsync(stream, new GuardRequest { Kind = "status" }, GuardJson.Default.GuardRequest, TestContext.Current.CancellationToken);
        await GuardWire.WriteAsync(stream, new GuardRequest { Kind = "clear" }, GuardJson.Default.GuardRequest, TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var chunks = new ChunkedStream(stream.ToArray());
        Assert.Equal("status", (await GuardWire.ReadAsync(chunks, GuardJson.Default.GuardRequest, TestContext.Current.CancellationToken)).Kind);
        Assert.Equal("clear", (await GuardWire.ReadAsync(chunks, GuardJson.Default.GuardRequest, TestContext.Current.CancellationToken)).Kind);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4194305)]
    public async Task Invalid_frame_length_is_rejected_before_payload_read(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => GuardWire.ReadAsync(stream, GuardJson.Default.GuardRequest, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Truncated_frame_fails_and_cancelled_read_does_not_wait()
    {
        using var stream = new MemoryStream([3, 0, 0, 0, (byte)'{']);
        await Assert.ThrowsAsync<EndOfStreamException>(() => GuardWire.ReadAsync(stream, GuardJson.Default.GuardRequest, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        stream.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GuardWire.ReadAsync(stream, GuardJson.Default.GuardRequest, cancelled.Token));
    }
    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
