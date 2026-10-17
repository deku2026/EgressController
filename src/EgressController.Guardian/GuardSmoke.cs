using System.Text.Json;
using EgressController.Core.Protection;
using EgressController.Diagnostics;

namespace EgressController.Guardian;

/// <summary>Safe executable entry used after NativeAOT publishing. No SCM, pipes, TUN or real process calls.</summary>
public static class GuardSmoke
{
    public static int Run()
    {
        var process = new GuardProcess(new(321, DateTime.UnixEpoch), 0, @"C:\mock\ai.exe");
        var policy = new GuardPolicy { MainExecutable = @"C:\mock\main.exe", Executables = [new(process.ExecutablePath, "Mock AI")] };
        var store = new MemoryStore { Policy = policy };
        var processes = new FakeProcesses(process);
        var runtime = new GuardRuntime(store, processes, new FakeFacts(), []);
        if (!runtime.Tick().Protecting || processes.Kills != 1) throw new InvalidOperationException("Cold-start guard failed.");
        Guid session = Guid.NewGuid();
        runtime.Connect(session, new(123, DateTime.UnixEpoch), policy.MainExecutable);
        var heartbeat = new GuardHeartbeat(policy.Revision, true, new(456, DateTime.UnixEpoch), @"C:\mock\sing-box.exe", null);
        if (!runtime.Report(session, 1, heartbeat) || runtime.Tick().Protecting) throw new InvalidOperationException("TUN lease failed.");
        runtime.Disconnect(session);
        if (!runtime.Tick().Protecting || processes.Kills != 2) throw new InvalidOperationException("Disconnect guard failed.");
        var request = new GuardRequest { Kind = "heartbeat", Session = session, Sequence = 3, Heartbeat = heartbeat };
        if (JsonSerializer.Deserialize(JsonSerializer.Serialize(request, GuardJson.Default.GuardRequest), GuardJson.Default.GuardRequest)?.Heartbeat != heartbeat)
            throw new InvalidOperationException("AOT protocol serialization failed.");
        string directory = Path.Combine(Path.GetTempPath(), "egress-guard-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new DiagnosticJournal(Path.Combine(directory, "mock.log"));
            diagnostics.Report("mock", "mock.failure", "Authorization: Bearer aot-secret", new IOException("mock disk detail"));
            diagnostics.Info("mock", "retrying");
            if (diagnostics.Snapshot().Count != 1 || File.ReadAllText(diagnostics.LogPath).Contains("aot-secret", StringComparison.Ordinal))
                throw new InvalidOperationException("AOT diagnostic persistence/redaction failed.");
            var database = new GuardStore(directory);
            database.SavePolicy(policy);
            database.SaveCheckpoint(new([], runtime.Status.Events));
            if (database.LoadPolicy()?.Revision != policy.Revision || database.LoadCheckpoint().Events.Length != 2)
                throw new InvalidOperationException("AOT SQLite persistence failed.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return 0;
    }
    private sealed class MemoryStore : IGuardStore
    {
        public GuardPolicy? Policy;
        public GuardPolicy? LoadPolicy() => Policy;
        public void SavePolicy(GuardPolicy policy) => Policy = policy;
        public GuardCheckpoint LoadCheckpoint() => new([], []);
        public void SaveCheckpoint(GuardCheckpoint checkpoint) { }
    }
    private sealed class FakeProcesses(GuardProcess process) : IProcessControl
    {
        public int Kills;
        public IReadOnlyList<GuardProcess> Capture() => [process];
        public TerminationResult Terminate(GuardProcess target) { Kills++; return new(true); }
    }
    private sealed class FakeFacts : IGuardFacts
    {
        public bool IsAlive(ProcessKey identity, string executable) => true;
        public string? InspectTun(ProcessKey core, string executable) => null;
    }
}
