using EgressController.Core.Models;
using EgressController.Launcher.Ownership;
using EgressController.Launcher.Sessions;

namespace EgressController.Launcher.Tests;

public class OwnedRootAndRegistryTests
{
    private static LaunchTarget Target(string[] roots, string[] executables = null!)
        => new()
        {
            Id = "t", Name = "T",
            OwnedRoots = roots,
            OwnedExecutables = executables ?? Array.Empty<string>(),
        };

    [Fact]
    public void Exe_under_root_is_owned()
        => Assert.True(OwnedRootMatcher.IsOwned(@"C:\ExampleApp\watcher.exe", Target([@"C:\ExampleApp"])));

    [Fact]
    public void Root_never_claims_prefix_sibling()
    {
        // C:\ExampleApp-Evil is NOT under C:\ExampleApp — plain prefix must not false-positive.
        Assert.False(OwnedRootMatcher.IsOwned(@"C:\ExampleApp-Evil\fake.exe", Target([@"C:\ExampleApp"])));
        Assert.False(OwnedRootMatcher.IsOwned(@"C:\ExampleApp2\fake.exe", Target([@"C:\ExampleApp"])));
        Assert.False(OwnedRootMatcher.IsOwned(@"C:\ExampleApp-something\fake.exe", Target([@"C:\ExampleApp"])));
    }

    [Fact]
    public void Exact_root_dir_is_owned()
        => Assert.True(OwnedRootMatcher.IsOwned(@"C:\ExampleApp", Target([@"C:\ExampleApp"])));

    [Fact]
    public void Owned_executable_is_owned_and_other_paths_are_not()
    {
        var t = Target(Array.Empty<string>(), [@"C:\App\special-helper.exe"]);
        Assert.True(OwnedRootMatcher.IsOwned(@"C:\App\special-helper.exe", t));
        Assert.False(OwnedRootMatcher.IsOwned(@"C:\App\other.exe", t));
    }

    [Fact]
    public void Scanned_descendant_requires_exact_executable_membership()
    {
        var t = Target([@"C:\App"], [@"C:\App\known-helper.exe"]);

        Assert.True(OwnedRootMatcher.IsScannedExecutable(@"c:/app/known-helper.exe", t));
        Assert.False(OwnedRootMatcher.IsScannedExecutable(@"C:\App\external-tool.exe", t));
        // Sharing the same directory is not enough for a child process to become Managed.
        Assert.False(OwnedRootMatcher.IsScannedExecutable(@"C:\App\new-helper.exe", t));
    }

    [Fact]
    public void Null_or_blank_final_path_is_never_owned()
    {
        var t = Target([@"C:\ExampleApp"]);
        Assert.False(OwnedRootMatcher.IsOwned(null, t));
        Assert.False(OwnedRootMatcher.IsOwned("", t));
    }

    [Fact]
    public void Matching_is_case_insensitive_and_separator_tolerant()
        => Assert.True(OwnedRootMatcher.IsOwned(@"c:\exampleapp\WATCHER.EXE", Target([@"C:\ExampleApp"])));

    [Fact]
    public void Registry_tracks_root_and_owned_membership()
    {
        var reg = new LaunchSessionRegistry();
        var s = new LaunchSession
        {
            SessionId = Guid.NewGuid(), TargetId = "t",
            RootPid = 111, RootStartTimeUtc = DateTime.UtcNow.AddSeconds(-5),
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-5),
        };
        reg.Register(s);
        Assert.Contains(s.SessionId, reg.SessionsForPid(111));

        reg.SetActiveOwnedPids(s.SessionId, new HashSet<uint> { 222, 333 });
        Assert.Contains(s.SessionId, reg.SessionsForPid(222));
        Assert.Contains(s.SessionId, reg.SessionsForPid(333));
        // after swapping owned set, the removed pid is no longer a member
        reg.SetActiveOwnedPids(s.SessionId, new HashSet<uint> { 222 });
        Assert.DoesNotContain(s.SessionId, reg.SessionsForPid(333));

        reg.Unregister(s.SessionId);
        Assert.Empty(reg.SessionsForPid(111));
        Assert.Empty(reg.SessionsForPid(222));
    }

    [Fact]
    public void Root_exit_removes_root_membership_but_keeps_verified_children_until_retired()
    {
        var reg = new LaunchSessionRegistry();
        var s = new LaunchSession
        {
            SessionId = Guid.NewGuid(), TargetId = "t",
            RootPid = 111, RootStartTimeUtc = DateTime.UtcNow.AddSeconds(-5),
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-5),
        };
        reg.Register(s);
        reg.SetActiveOwnedPids(s.SessionId, new HashSet<uint> { 111, 222 });

        Assert.True(reg.MarkRootExited(s.SessionId));
        Assert.True(s.RootExited);
        Assert.DoesNotContain(s.SessionId, reg.SessionsForPid(111));
        Assert.Contains(s.SessionId, reg.SessionsForPid(222));

        Assert.Equal([s.SessionId], reg.UnregisterForTarget("t"));
        Assert.Empty(reg.All());
    }

    [Fact]
    public void Reconciliation_preserves_an_accept_time_promotion_that_arrived_after_its_baseline()
    {
        DateTime rootStarted = DateTime.UtcNow.AddSeconds(-5);
        var reg = new LaunchSessionRegistry();
        var session = new LaunchSession
        {
            SessionId = Guid.NewGuid(),
            TargetId = "t",
            RootPid = 111,
            RootStartTimeUtc = rootStarted,
            StartedAtUtc = rootStarted,
            CandidatePids = new HashSet<uint> { 111 },
            ActiveOwnedPids = new HashSet<uint> { 111 },
            ActiveOwnedProcessStartTimes = new Dictionary<uint, DateTime> { [111] = rootStarted },
        };
        reg.Register(session);

        IReadOnlySet<uint> baselineCandidates = session.CandidatePids.ToHashSet();
        IReadOnlyDictionary<uint, DateTime> baselineOwned =
            new Dictionary<uint, DateTime>(session.ActiveOwnedProcessStartTimes);
        DateTime childStarted = rootStarted.AddSeconds(1);
        Assert.True(reg.TrackOwnedProcess(
            session.SessionId,
            new ProcessIdentity(222, childStarted, @"C:\App\helper.exe", @"C:\App\helper.exe")));

        reg.ReconcileMembership(
            session.SessionId,
            baselineCandidates,
            baselineOwned,
            new HashSet<uint> { 111 },
            new Dictionary<uint, DateTime> { [111] = rootStarted });

        LaunchSession current = reg.Get(session.SessionId)!;
        Assert.Contains((uint)222, current.CandidatePids);
        Assert.Equal(childStarted, current.ActiveOwnedProcessStartTimes[222]);
        Assert.Contains(session.SessionId, reg.SessionsForPid(222));
    }
}
