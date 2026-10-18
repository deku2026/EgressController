using EgressController.Core.Contracts;
using EgressController.Core.Protection;

namespace EgressController.Guardian.Tests;

public sealed class GuardReviewRegressionTests
{
    [Fact]
    public void Unreadable_saved_helper_prevents_successful_transition_ack()
    {
        var processes = new Processes();
        processes.Snapshot = [Fixture.Process(), Fixture.Process(11, @"C:\Shared\child.exe", 10, 2)];
        var runtime = new GuardRuntime(new FakeStore { Policy = Fixture.Policy with { Executables = [.. Fixture.Policy.Executables, new(@"C:\Shared\child.exe", "AI")] } }, processes, new FakeFacts(), []);
        runtime.Tick();
        processes.Snapshot = [];
        processes.Failures = [new(11, "child.exe", "mock access denied")];
        var status = runtime.PrepareTransition();
        Assert.Equal(1, status.FailedCount);
        Assert.True(status.Protecting);
        Assert.Contains(status.Events, item => item.Pid == 11 && !item.Succeeded);
    }

    [Fact]
    public void Saved_junction_executable_keeps_revision_and_matches_resolved_path_after_service_restart()
    {
        var policy = Fixture.Policy with
        {
            Executables = [new(@"C:\Link\new.exe", "AI")], Directories = [new(@"C:\Link", "AI")],
            Exclusions = [@"C:\mock\controller.exe", @"C:\Link\proxy.exe"]
        };
        var store = new FakeStore { Policy = policy };
        var processes = new Processes { Snapshot =
            [Fixture.Process(path: @"D:\Real\new.exe"), Fixture.Process(11, @"D:\Real\proxy.exe")] };
        var runtime = new GuardRuntime(store, processes, new FakeFacts(), [], pathResolver: new Resolver());
        runtime.SetPolicy(policy); // expansion must not make an identical revision look different
        runtime.Tick();
        Assert.Equal(10u, Assert.Single(processes.Killed).Pid);
        Assert.Equal(policy, store.Policy);
        Assert.Throws<InvalidDataException>(() => runtime.SetPolicy(policy with { Directories = [] }));
    }

    private sealed class Resolver : IExecutablePathCanonicalizer
    {
        public string? Canonicalize(string path) => path.StartsWith(@"C:\Link", StringComparison.OrdinalIgnoreCase)
            ? @"D:\Real" + path[7..] : path;
    }
    private sealed class Processes : IProcessControl
    {
        public IReadOnlyList<GuardProcess> Snapshot = [];
        public IReadOnlyList<ProcessInspectionFailure> Failures = [];
        public IReadOnlyList<ProcessInspectionFailure> InspectionFailures => Failures;
        public List<ProcessKey> Killed = [];
        public IReadOnlyList<GuardProcess> Capture() => Snapshot;
        public TerminationResult Terminate(GuardProcess process) { Killed.Add(process.Key); return new(true); }
    }
}
