// SPDX-License-Identifier: MIT
using System.Text;
using EgressController.Mac;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Mac.Tests;

public sealed class ProductionBoundaryTests
{
    [Fact]
    public async Task FramedProductionRequestsAndRepliesRoundTripWithoutAnySocket()
    {
        var request = new MacHelperWireRequest(2, Guid.NewGuid(), 7, MacHelperCommand.Inspect);
        using var input = new MemoryStream(MacHelperWireCodec.EncodeRequest(request));
        Assert.Equal(request, await MacHelperWireCodec.ReadRequestAsync(input, TestContext.Current.CancellationToken));
        var reply = new MacHelperWireReply(request.SessionId, request.Sequence, true, "ok", "", Status());
        using var output = new MemoryStream(); await MacHelperWireCodec.WriteReplyAsync(output, reply, TestContext.Current.CancellationToken);
        Assert.Equal(reply, MacHelperWireCodec.DecodeReply(output.ToArray()));
        Assert.Throws<InvalidDataException>(() => MacHelperWireCodec.DecodeReply(new byte[] { 0x7f, 0xff, 0xff, 0xff }));
    }

    [Fact]
    public async Task AuthenticatedClientRejectsStaleOrWrongSessionRepliesAndNeverKeepsHealthyStatus()
    {
        var connection = new FixtureConnection(); await using var client = new MacHelperClient(connection);
        Assert.True((await client.InspectAsync(TestContext.Current.CancellationToken)).Installed);
        connection.WrongSequence = true;
        Assert.False((await client.InspectAsync(TestContext.Current.CancellationToken)).TunTakenOver);
        Assert.False(client.Status.Running);
        connection.WrongSequence = false; connection.Stale = true;
        Assert.False((await client.InspectAsync(TestContext.Current.CancellationToken)).TunTakenOver);
        Assert.Single(connection.Requests.Select(request => request.SessionId).Distinct());
        Assert.Equal(new long[] { 1, 2, 3 }, connection.Requests.Select(request => request.Sequence));
    }

    [Fact]
    public async Task RealRuntimeAdapterUsesInjectedStagingAndClientOnlyAndRestoresOldInterfaceIds()
    {
        var document = new MacPolicyDocument
        {
            Interfaces = new() { HomeId = "home-before-edit", NonHomeId = "nonhome-before-edit" },
            Defaults = new() { Global = ExitChoice.Proxy("node") },
            Nodes = [new() { Id = "node", SourceId = "test", Name = "Node", Protocol = NodeProtocol.Vless,
                Server = "proxy.example.invalid", Port = 443, Tls = true, UserId = "11111111-1111-1111-1111-111111111111" }]
        };
        string policy = PolicyYaml.Write(document), json = "{}";
        var assets = new Dictionary<string, byte[]>();
        var generation = new RuntimeGeneration(ConfigurationCoordinator.GenerationIdentity(policy, json, assets), policy, json, assets);
        var connection = new FixtureConnection(); await using var client = new MacHelperClient(connection);
        var stager = new FixtureStager(); int resolutions = 0;
        var runtime = new MacXrayCoreRuntime(client, stager, _ =>
        { resolutions++; return Task.FromResult(new MacRuntimeLaunch("/mock/xray", new string('a', 64), "edited-home", "edited-other")); });
        await runtime.ValidateAsync(generation, TestContext.Current.CancellationToken);
        await runtime.ApplyAsync(generation, TestContext.Current.CancellationToken);
        Assert.Equal(1, resolutions);
        Assert.Equal("home-before-edit", stager.LastLaunch!.HomeServiceId);
        Assert.Equal("nonhome-before-edit", stager.LastLaunch.NonHomeServiceId);
        await runtime.RestoreAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { MacHelperCommand.Validate, MacHelperCommand.Apply, MacHelperCommand.StopAndRestore }, connection.Requests.Select(request => request.Command));
    }

    [Fact]
    public void InstallerPlansAreExplicitAndShellArgumentsCannotBecomeCommands()
    {
        var plan = InstallPlan() with { SourceHelperPath = "/Users/test/helper'; $(touch forbidden); '" };
        string script = MacHelperInstaller.BuildAuthorizationScript(plan, "/Users/test/staging/plan.json", new string('f', 64));
        Assert.Contains("'\\''", script, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/shasum -a 256", script, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/plutil -replace SourceHelperPath", script, StringComparison.Ordinal);
        Assert.Contains("-d -o root -g wheel -m 755 /Library/PrivilegedHelperTools", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("[ ! -L /Library/PrivilegedHelperTools ]", StringComparison.Ordinal) < script.IndexOf("mktemp", StringComparison.Ordinal));
        Assert.EndsWith("--install \"$egress_install_stage/install-plan.json\"", script, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => MacHelperInstaller.ValidatePlan(plan with { Installation = plan.Installation with { AuthorizedGuiPath = "/usr/local/share/dotnet/dotnet" } }));
        Assert.Throws<InvalidDataException>(() => MacHelperInstaller.ValidatePlan(null!));
        Assert.Throws<InvalidDataException>(() => MacHelperInstaller.ValidatePlan(plan with { Installation = null! }));
        Assert.Throws<InvalidDataException>(() => MacHelperInstaller.ValidatePlan(plan with { Installation = plan.Installation with { XraySha256 = null! } }));
    }

    [Fact]
    public async Task HeartbeatTimeoutIsUnhealthyAndTheNextHeartbeatCanRecoverWithoutNativeCalls()
    {
        var connection = new RecoveringHeartbeat(); await using var client = new MacHelperClient(connection, TimeSpan.FromMilliseconds(10));
        var plan = new MacHelperApplyPlan("generation", "/mock/generation", new string('f', 64), "/mock/xray", new string('a', 64), "home", "other");
        Assert.True((await client.ApplyAsync(plan, TestContext.Current.CancellationToken)).Succeeded);
        await connection.Failed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        // Requests are serialized: entering the retry proves the timeout was handled and its
        // unhealthy status published. Hold that retry so it cannot recover before this assertion.
        await connection.RecoveryAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(client.Status.TunTakenOver);
        connection.AllowRecovery.TrySetResult();
        // A following heartbeat can enter only after the successful retry has published its
        // status and released the request lock; transport completion alone is too early.
        await connection.FollowingHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(client.Status.TunTakenOver);
    }

    [Fact]
    public async Task MockGateBlocksEveryNewProductionBoundaryBeforeAnyNativeOrFilesystemAction()
    {
        var gate = new LiveOperationGate(); var native = new NativeMacPrivileges(gate);
        Assert.Throws<InvalidOperationException>(() => native.ReadPeer(-1));
        Assert.Throws<InvalidOperationException>(() => native.ReadProcess(-1));
        Assert.Throws<InvalidOperationException>(() => native.OpenOwnedRead("/not-read", 501, 1));
        Assert.Throws<InvalidOperationException>(() => native.SetDns([], "1.1.1.1"));
        Assert.Throws<InvalidOperationException>(() => native.RestoreDns([]));
        Assert.Throws<InvalidOperationException>(() => native.IsTunTakeoverActive("utun99", "198.18.0.1/30"));
        Assert.Throws<InvalidOperationException>(() => native.BindSocket(null!, "en0"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MacHelperInstaller(gate).InstallAsync(InstallPlan(), TestContext.Current.CancellationToken));
    }

    private static MacHelperInstallPlan InstallPlan() => new("/Users/test/helper", "/Users/test/native.dylib", "/Users/test/xray",
        new(2, 501, 20, "/Applications/Egress.app/Contents/MacOS/EgressController.Desktop", new string('a', 64),
            "/Users/test/staging", new string('b', 64), new string('c', 64), new string('d', 64)));
    private static MacHelperStatus Status() => new(true, true, true, false, 1234, "generation", null, DateTimeOffset.UtcNow, new string('a', 64));
    private sealed class FixtureConnection : IMacHelperConnection
    {
        public List<MacHelperWireRequest> Requests { get; } = [];
        public bool WrongSequence, Stale;
        public Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            var status = Status();
            if (request.Command == MacHelperCommand.StopAndRestore) status = status with { Running = false, TunTakenOver = false, DnsRestored = true };
            if (Stale) status = status with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
            return Task.FromResult(new MacHelperWireReply(request.SessionId, request.Sequence + (WrongSequence ? 1 : 0), true, "ok", "", status));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FixtureStager : IMacGenerationStager
    {
        public MacRuntimeLaunch? LastLaunch;
        public Task<MacHelperApplyPlan> StageAsync(RuntimeGeneration generation, MacRuntimeLaunch launch, CancellationToken cancellationToken)
        { LastLaunch = launch; return Task.FromResult(new MacHelperApplyPlan(generation.Id, "/mock/staged", new string('e', 64), launch.XrayPath, launch.XraySha256, launch.HomeServiceId, launch.NonHomeServiceId)); }
    }
    private sealed class RecoveringHeartbeat : IMacHelperConnection
    {
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FollowingHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _heartbeats;
        public async Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken cancellationToken)
        {
            if (request.Command == MacHelperCommand.Heartbeat)
            {
                int heartbeat = Interlocked.Increment(ref _heartbeats);
                if (heartbeat == 1) { Failed.TrySetResult(); throw new OperationCanceledException("Injected transport timeout."); }
                if (heartbeat == 2)
                {
                    RecoveryAttempted.TrySetResult();
                    await AllowRecovery.Task.WaitAsync(cancellationToken);
                }
                else FollowingHeartbeat.TrySetResult();
            }
            return new(request.SessionId, request.Sequence, true, "ok", "", Status());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
