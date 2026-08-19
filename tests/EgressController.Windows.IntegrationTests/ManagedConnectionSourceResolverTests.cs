using System.Net;
using EgressController.App;
using EgressController.Core.Contracts;
using EgressController.Core.Models;
using EgressController.Launcher.Discovery;
using EgressController.Launcher.Sessions;

namespace EgressController.Windows.IntegrationTests;

public sealed class ManagedConnectionSourceResolverTests
{
    [Fact]
    public void First_connection_promotes_a_verified_scanned_descendant_immediately()
    {
        DateTime rootStarted = DateTime.UtcNow.AddSeconds(-2);
        const string rootExe = @"C:\Apps\Sample\Sample.exe";
        const string childExe = @"C:\Apps\Sample\SampleNetwork.exe";
        var target = NewTarget(rootExe, childExe);
        var targets = new LaunchTargetRegistry();
        Assert.True(targets.Add(target));

        var session = NewSession(target.Id, rootStarted);
        var sessions = new LaunchSessionRegistry();
        sessions.Register(session);

        var identities = new Dictionary<uint, ProcessIdentity>
        {
            [100] = new(100, rootStarted, rootExe, rootExe),
            [101] = new(101, rootStarted.AddMilliseconds(200), childExe, childExe),
        };
        var resolver = new ManagedConnectionSourceResolver(
            new FixedOwnerResolver(101),
            new DictionaryIdentityResolver(identities),
            sessions,
            targets,
            (root, child) => root == 100 && child == 101);

        ProxySource? source = resolver.Resolve(
            new IPEndPoint(IPAddress.Loopback, 50000),
            new IPEndPoint(IPAddress.Loopback, 18080),
            CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal(session.SessionId.ToString("D"), source.SessionId);
        Assert.Equal("SampleNetwork.exe", source.ProcessName);
        Assert.Contains((uint)101, sessions.Get(session.SessionId)!.ActiveOwnedPids);
        Assert.Contains((uint)101, sessions.Get(session.SessionId)!.CandidatePids);
    }

    [Fact]
    public void Reused_root_pid_cannot_promote_a_descendant()
    {
        DateTime rootStarted = DateTime.UtcNow.AddSeconds(-2);
        const string rootExe = @"C:\Apps\Sample\Sample.exe";
        const string childExe = @"C:\Apps\Sample\SampleNetwork.exe";
        var target = NewTarget(rootExe, childExe);
        var targets = new LaunchTargetRegistry();
        Assert.True(targets.Add(target));

        var session = NewSession(target.Id, rootStarted);
        var sessions = new LaunchSessionRegistry();
        sessions.Register(session);
        var identities = new Dictionary<uint, ProcessIdentity>
        {
            [100] = new(100, rootStarted.AddMinutes(1), rootExe, rootExe),
            [101] = new(101, rootStarted.AddMilliseconds(200), childExe, childExe),
        };
        var resolver = new ManagedConnectionSourceResolver(
            new FixedOwnerResolver(101),
            new DictionaryIdentityResolver(identities),
            sessions,
            targets,
            (_, _) => true);

        ProxySource? source = resolver.Resolve(
            new IPEndPoint(IPAddress.Loopback, 50000),
            new IPEndPoint(IPAddress.Loopback, 18080),
            CancellationToken.None);

        Assert.NotNull(source);
        Assert.Null(source.SessionId);
        Assert.DoesNotContain((uint)101, sessions.Get(session.SessionId)!.ActiveOwnedPids);
    }

    [Fact]
    public void Same_scanned_executable_outside_this_launch_tree_stays_unmanaged()
    {
        DateTime rootStarted = DateTime.UtcNow.AddSeconds(-2);
        const string rootExe = @"C:\Apps\Sample\Sample.exe";
        const string childExe = @"C:\Apps\Sample\SampleNetwork.exe";
        var target = NewTarget(rootExe, childExe);
        var targets = new LaunchTargetRegistry();
        Assert.True(targets.Add(target));

        var session = NewSession(target.Id, rootStarted);
        var sessions = new LaunchSessionRegistry();
        sessions.Register(session);
        var identities = new Dictionary<uint, ProcessIdentity>
        {
            [100] = new(100, rootStarted, rootExe, rootExe),
            [101] = new(101, rootStarted.AddMilliseconds(200), childExe, childExe),
        };
        var resolver = new ManagedConnectionSourceResolver(
            new FixedOwnerResolver(101),
            new DictionaryIdentityResolver(identities),
            sessions,
            targets,
            (_, _) => false);

        ProxySource? source = resolver.Resolve(
            new IPEndPoint(IPAddress.Loopback, 50000),
            new IPEndPoint(IPAddress.Loopback, 18080),
            CancellationToken.None);

        Assert.NotNull(source);
        Assert.Null(source.SessionId);
        Assert.DoesNotContain((uint)101, sessions.Get(session.SessionId)!.ActiveOwnedPids);
    }

    [Fact]
    public void Tracking_job_promotes_a_scanned_process_after_parent_chain_is_lost()
    {
        DateTime rootStarted = DateTime.UtcNow.AddSeconds(-2);
        const string rootExe = @"C:\Apps\Sample\Sample.exe";
        const string browserExe = @"C:\Apps\Sample\BrowserHost.exe";
        var target = NewTarget(rootExe, browserExe);
        var targets = new LaunchTargetRegistry();
        Assert.True(targets.Add(target));

        var session = NewSession(target.Id, rootStarted);
        var sessions = new LaunchSessionRegistry();
        sessions.Register(session);
        var identities = new Dictionary<uint, ProcessIdentity>
        {
            [100] = new(100, rootStarted, rootExe, rootExe),
            [102] = new(102, rootStarted.AddSeconds(1), browserExe, browserExe),
        };
        var resolver = new ManagedConnectionSourceResolver(
            new FixedOwnerResolver(102),
            new DictionaryIdentityResolver(identities),
            sessions,
            targets,
            (_, _) => false,
            (sessionId, pid) => sessionId == session.SessionId && pid == 102);

        ProxySource? source = resolver.Resolve(
            new IPEndPoint(IPAddress.Loopback, 50000),
            new IPEndPoint(IPAddress.Loopback, 18080),
            CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal(session.SessionId.ToString("D"), source.SessionId);
        Assert.Equal(rootStarted.AddSeconds(1),
            sessions.Get(session.SessionId)!.ActiveOwnedProcessStartTimes[102]);
    }

    [Fact]
    public void Reused_owned_child_pid_does_not_inherit_managed_membership()
    {
        DateTime rootStarted = DateTime.UtcNow.AddSeconds(-5);
        const string rootExe = @"C:\Apps\Sample\Sample.exe";
        const string childExe = @"C:\Apps\Sample\SampleNetwork.exe";
        var target = NewTarget(rootExe, childExe);
        var targets = new LaunchTargetRegistry();
        Assert.True(targets.Add(target));

        var session = NewSession(target.Id, rootStarted);
        var sessions = new LaunchSessionRegistry();
        sessions.Register(session);
        Assert.True(sessions.TrackOwnedProcess(
            session.SessionId,
            new ProcessIdentity(101, rootStarted.AddSeconds(1), childExe, childExe)));

        var identities = new Dictionary<uint, ProcessIdentity>
        {
            [100] = new(100, rootStarted, rootExe, rootExe),
            // Same PID and same executable, but a later process identity.
            [101] = new(101, rootStarted.AddSeconds(3), childExe, childExe),
        };
        var resolver = new ManagedConnectionSourceResolver(
            new FixedOwnerResolver(101),
            new DictionaryIdentityResolver(identities),
            sessions,
            targets,
            (_, _) => false,
            (_, _) => false);

        ProxySource? source = resolver.Resolve(
            new IPEndPoint(IPAddress.Loopback, 50000),
            new IPEndPoint(IPAddress.Loopback, 18080),
            CancellationToken.None);

        Assert.NotNull(source);
        Assert.Null(source.SessionId);
    }

    private static LaunchTarget NewTarget(string rootExe, string childExe)
        => new()
        {
            Id = "sample",
            Name = "Sample",
            Kind = LaunchKind.DirectExe,
            Command = rootExe,
            CanonicalExecutable = rootExe,
            OwnedRoots = [Path.GetDirectoryName(rootExe)!],
            OwnedExecutables = [rootExe, childExe],
            Managed = true,
        };

    private static LaunchSession NewSession(string targetId, DateTime rootStarted)
        => new()
        {
            SessionId = Guid.NewGuid(),
            TargetId = targetId,
            RootPid = 100,
            RootStartTimeUtc = rootStarted,
            StartedAtUtc = rootStarted,
            CandidatePids = new HashSet<uint> { 100 },
            ActiveOwnedPids = new HashSet<uint> { 100 },
            ActiveOwnedProcessStartTimes = new Dictionary<uint, DateTime>
            {
                [100] = rootStarted,
            },
        };

    private sealed class FixedOwnerResolver(uint pid) : IConnectionOwnerResolver
    {
        public uint? ResolveOwner(IPEndPoint clientLocal, IPEndPoint listenerLocal, CancellationToken cancellationToken)
            => pid;
    }

    private sealed class DictionaryIdentityResolver(IReadOnlyDictionary<uint, ProcessIdentity> identities)
        : IProcessIdentityResolver
    {
        public ProcessIdentity? Resolve(uint pid)
            => identities.TryGetValue(pid, out ProcessIdentity? identity) ? identity : null;
    }
}
