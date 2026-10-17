using System.Runtime.CompilerServices;
using EgressController.Core.Protection;

namespace EgressController.Guardian.Tests;

internal static class MockOnly
{
    [ModuleInitializer]
    public static void Initialize() => Environment.SetEnvironmentVariable("EGRESS_MOCK_ONLY", "1");
}
internal sealed class FakeClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
    public void Advance(TimeSpan value) => _ticks += value.Ticks;
}
internal sealed class FakeProcesses : IProcessControl
{
    public List<GuardProcess> Snapshot = [];
    public List<ProcessKey> Killed = [];
    public TerminationResult Result = new(true);
    public bool CaptureFails;
    public IReadOnlyList<GuardProcess> Capture() => CaptureFails ? throw new IOException("mock capture failure") : Snapshot;
    public TerminationResult Terminate(GuardProcess process) { Killed.Add(process.Key); return Result; }
}
internal sealed class FakeFacts : IGuardFacts
{
    public bool MainAlive = true;
    public string? TunError = null;
    public bool Throw;
    public bool IsAlive(ProcessKey identity, string executable) => Throw ? throw new IOException("mock identity failure") : MainAlive;
    public string? InspectTun(ProcessKey core, string executable) => TunError;
}
internal sealed class FakeStore : IGuardStore
{
    public GuardPolicy? Policy = Fixture.Policy;
    public GuardCheckpoint Checkpoint = new([], []);
    public bool PolicyWriteFails;
    public bool CheckpointWriteFails;
    public int Writes;
    public GuardPolicy? LoadPolicy() => Policy;
    public void SavePolicy(GuardPolicy policy)
    {
        if (PolicyWriteFails) throw new IOException("mock disk full");
        Policy = policy; Writes++;
    }
    public GuardCheckpoint LoadCheckpoint() => Checkpoint;
    public void SaveCheckpoint(GuardCheckpoint checkpoint)
    {
        if (CheckpointWriteFails) throw new IOException("mock checkpoint failure");
        Checkpoint = checkpoint; Writes++;
    }
}
internal static class Fixture
{
    public static GuardPolicy Policy => new()
    {
        Revision = "11111111111111111111111111111111", MainExecutable = @"C:\mock\controller.exe",
        Executables = [new(@"C:\apps\AI\ai.exe", "AI")],
        Directories = [new(@"C:\apps\AI", "AI")],
        Exclusions = [@"C:\mock\controller.exe", @"C:\apps\AI\proxy.exe"],
    };
    public static GuardProcess Process(uint pid = 10, string path = @"C:\apps\AI\ai.exe", uint parent = 0, int seconds = 1)
        => new(new(pid, DateTime.UnixEpoch.AddSeconds(seconds)), parent, path);
    public static GuardHeartbeat Heartbeat(GuardPolicy? policy = null) => new((policy ?? Policy).Revision, true,
        new(500, DateTime.UnixEpoch), @"C:\mock\sing-box.exe", null);
    public static ProcessKey Main => new(123, DateTime.UnixEpoch);
}
internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "egress-guard-test-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
