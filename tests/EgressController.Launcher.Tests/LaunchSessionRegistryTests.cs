using EgressController.Core.Models;
using EgressController.Launcher.Sessions;

namespace EgressController.Launcher.Tests;

public sealed class LaunchSessionRegistryTests
{
    [Fact]
    public void Registry_tracks_and_replaces_process_membership()
    {
        var registry = new LaunchSessionRegistry();
        var session = NewSession();
        registry.Register(session);

        Assert.Contains(session.SessionId, registry.SessionsForPid(111));
        registry.SetActiveOwnedPids(session.SessionId, new HashSet<uint> { 222, 333 });
        Assert.Contains(session.SessionId, registry.SessionsForPid(222));
        Assert.Contains(session.SessionId, registry.SessionsForPid(333));
        registry.SetActiveOwnedPids(session.SessionId, new HashSet<uint> { 222 });
        Assert.DoesNotContain(session.SessionId, registry.SessionsForPid(333));

        registry.Unregister(session.SessionId);
        Assert.Empty(registry.All());
    }

    [Fact]
    public void Root_exit_retains_verified_children_until_the_session_is_removed()
    {
        var registry = new LaunchSessionRegistry();
        var session = NewSession();
        registry.Register(session);
        registry.SetActiveOwnedPids(session.SessionId, new HashSet<uint> { 111, 222 });

        Assert.True(registry.MarkRootExited(session.SessionId));
        Assert.True(session.RootExited);
        Assert.DoesNotContain(session.SessionId, registry.SessionsForPid(111));
        Assert.Contains(session.SessionId, registry.SessionsForPid(222));
        Assert.Equal([session.SessionId], registry.UnregisterForTarget("target"));
    }

    private static LaunchSession NewSession()
    {
        DateTime started = DateTime.UtcNow.AddSeconds(-5);
        return new LaunchSession
        {
            SessionId = Guid.NewGuid(),
            TargetId = "target",
            RootPid = 111,
            RootStartTimeUtc = started,
            StartedAtUtc = started,
        };
    }
}
