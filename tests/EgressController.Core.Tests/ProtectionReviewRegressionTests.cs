using System.Net;
using EgressController.Core.Contracts;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;

namespace EgressController.Core.Tests;

public sealed class ProtectionReviewRegressionTests
{
    private static readonly HashSet<string> Exclusions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ProtectedExecutable[] Targets = [new(@"C:\Apps\AI\app.exe", "AI")];
    private static readonly GuardProcess Parent = new(new(10, DateTime.UnixEpoch), 0, Targets[0].Path);
    private static readonly GuardProcess Child = new(new(11, DateTime.UnixEpoch.AddSeconds(1)), 10, @"C:\Shared\worker.exe");

    [Fact]
    public void Temporarily_unreadable_orphan_keeps_identity_and_blocks_handoff_until_confirmed()
    {
        var processes = new Processes { Snapshot = [Parent, Child] };
        var protection = new ProcessProtection(processes);
        protection.Sweep(Targets, Exclusions, true, "ready");
        processes.Snapshot = [];
        processes.Failures = [new(11, "worker.exe", "access denied")];
        Assert.Equal(1, protection.Sweep(Targets, Exclusions, false, "handoff"));
        Assert.Equal(Child.Key, Assert.Single(protection.CaptureTracking()).Identity);
        Assert.Empty(processes.Killed);
        processes.Failures = [];
        processes.Snapshot = [Child];
        Assert.Equal(0, protection.Sweep(Targets, Exclusions, false, "handoff"));
        Assert.Equal(Child.Key, Assert.Single(processes.Killed));
    }

    [Fact]
    public void Unreadable_pid_is_not_killed_after_creation_time_proves_it_was_reused()
    {
        var processes = new Processes { Snapshot = [Parent, Child] };
        var protection = new ProcessProtection(processes);
        protection.Sweep(Targets, Exclusions, true, "ready");
        processes.Snapshot = [];
        processes.Failures = [new(11, "worker.exe", "access denied")];
        protection.Sweep(Targets, Exclusions, false, "handoff");
        processes.Failures = [];
        processes.Snapshot = [Child with { Key = new(11, DateTime.UnixEpoch.AddMinutes(1)), ParentPid = 0 }];
        Assert.Equal(0, protection.Sweep(Targets, Exclusions, false, "handoff"));
        Assert.Empty(processes.Killed);
        Assert.Empty(protection.CaptureTracking());
    }

    [Fact]
    public void New_external_child_waits_for_its_route_while_existing_app_stays_ready()
    {
        var processes = new Processes { Snapshot = [Parent, Child] };
        var protection = new ProcessProtection(processes);
        var oldRoutes = new ApplicationRoutingCoverage([Parent.ExecutablePath], []);
        protection.Sweep(Targets, Exclusions, true, "ready", canRun: oldRoutes.Contains);
        Assert.Equal(Child.Key, Assert.Single(processes.Killed));
        Assert.Contains("分流规则尚未生效", Assert.Single(protection.Events).Reason);
        Assert.Contains(protection.CaptureRoutingMembers(), item => item.Path == Child.ExecutablePath && item.Source == Targets[0]);
        processes.Killed.Clear();
        processes.Snapshot = [Child]; // parent exited, routing still inherits its selection
        var newRoutes = new ApplicationRoutingCoverage([Parent.ExecutablePath, Child.ExecutablePath], []);
        protection.Sweep(Targets, Exclusions, true, "ready", canRun: newRoutes.Contains);
        Assert.Empty(processes.Killed);
        protection.Sweep([], Exclusions, true, "ready", canRun: newRoutes.Contains);
        Assert.Empty(protection.CaptureRoutingMembers());
    }

    [Fact]
    public void Recursive_route_covers_future_executables_but_not_neighboring_directories()
    {
        var routes = new ApplicationRoutingCoverage([], [@"C:\Apps\AI"]);
        Assert.True(routes.Contains(@"c:\apps\ai\new\worker.EXE"));
        Assert.False(routes.Contains(@"C:\Apps\AI-copy\worker.exe"));
        Assert.False(routes.Contains(@"C:\Apps\AI\worker.dll"));
        var processes = new Processes { Snapshot = [Parent, Parent with { Key = new(12, DateTime.UnixEpoch), ExecutablePath = @"C:\Apps\AI\new\worker.exe" }] };
        var protection = new ProcessProtection(processes);
        protection.Sweep([], Exclusions, true, "ready", [new(@"C:\Apps\AI", "AI")], routes.Contains);
        Assert.Empty(processes.Killed);
        Assert.Equal(2, protection.CaptureTracking().Length);
    }

    [Fact]
    public void Observed_and_final_paths_match_selection_and_exclusions()
    {
        var process = Parent with { ExecutablePath = @"D:\Real\app.exe", ObservedExecutablePath = Parent.ExecutablePath };
        var processes = new Processes { Snapshot = [process] };
        var protection = new ProcessProtection(processes);
        protection.Sweep(Targets, Exclusions, false, "offline");
        Assert.Single(processes.Killed);
        processes.Killed.Clear();
        protection.Sweep(Targets, new HashSet<string>([process.ExecutablePath], StringComparer.OrdinalIgnoreCase), false, "offline");
        Assert.Empty(processes.Killed);
        Assert.True(new ApplicationRoutingCoverage([Parent.ExecutablePath], []).Contains(process));
    }

    [Fact]
    public void Profile_aliases_expand_files_and_directory_roots_without_losing_offline_paths()
    {
        var profile = new EgressProfileDocument
        {
            Applications = [new() { DiscoveryKey = @"exe:C:\Link\app.exe", DirectoryRoots = [@"C:\Link"] }]
        };
        var expanded = ApplicationPathAliases.Expand(profile, new AliasResolver());
        Assert.Contains(@"C:\Link\app.exe", expanded.Applications[0].ExecutablePaths);
        Assert.Contains(@"D:\Real\app.exe", expanded.Applications[0].ExecutablePaths);
        Assert.Equal(new[] { @"C:\Link", @"D:\Real" }, expanded.Applications[0].DirectoryRoots);
        Assert.Equal(new[] { @"C:\Offline\app.exe" }, ApplicationPathAliases.Expand([@"C:\Offline\app.exe"], new AliasResolver()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Packaged_storage_is_writable_and_portable_storage_keeps_existing_layout(bool packaged)
    {
        var paths = ApplicationStoragePaths.Resolve(@"C:\Program Files\WindowsApps\Egress", @"C:\Users\Test\AppData\Local", packaged);
        var root = packaged ? @"C:\Users\Test\AppData\Local\EgressController" : @"C:\Program Files\WindowsApps\Egress";
        Assert.Equal(Path.Combine(root, "data"), paths.Data);
        Assert.Equal(Path.Combine(root, "ruleset"), paths.Rulesets);
    }

    [Theory]
    [InlineData("Mihomo", "TUN", 53)]
    [InlineData("VPN", "WireGuard Tunnel", 6)]
    [InlineData("Other", "Tunnel", 131)]
    public void Foreign_active_tunnel_rejects_start_and_takeover(string name, string description, uint type)
    {
        var other = Adapter(7, name, description, type);
        Assert.Contains(name, TunTakeover.GetStartError([other], [], false));
        Assert.Contains(name, TunTakeover.GetError([Tun(), other], Routes()));
    }

    [Fact]
    public void Offline_vpn_and_normal_virtual_ethernet_do_not_block_start()
    {
        var offline = Adapter(7, "Mihomo", "Wintun", 53, up: false);
        var ethernet = Adapter(8, "vEthernet (WSL)", "Hyper-V Virtual Ethernet", 6);
        var routes = Routes().Concat([new TunDefaultRoute(8, false, IPAddress.Parse("172.16.0.0"), 12)]).ToArray();
        Assert.Null(TunTakeover.GetStartError([offline, ethernet], routes, false));
        Assert.Null(TunTakeover.GetError([Tun(), offline, ethernet], routes));
    }

    [Fact]
    public void Existing_managed_name_requires_a_known_owned_core_and_pins_adapter_identity()
    {
        var tun = Tun();
        Assert.NotNull(TunTakeover.GetStartError([tun], Routes(), false));
        Assert.Null(TunTakeover.GetStartError([tun], Routes(), true));
        Assert.Null(TunTakeover.GetError([tun], Routes(), tun.Identity.Guid));
        Assert.Contains("身份", TunTakeover.GetError([tun], Routes(), Guid.NewGuid()));
    }

    [Theory]
    [InlineData(false, "0.0.0.0")]
    [InlineData(false, "128.0.0.0")]
    [InlineData(true, "8000::")]
    public void More_specific_public_routes_override_tun_even_with_worse_metric(bool ipv6, string prefix)
    {
        var other = Adapter(7, "Ethernet", "Physical", 6);
        var routes = Routes().Concat([new TunDefaultRoute(7, ipv6, IPAddress.Parse(prefix), 1, 9999)]).ToArray();
        Assert.Contains("路由冲突", TunTakeover.GetError([Tun(), other], routes));
    }

    [Theory]
    [InlineData(0u, 10u, false)] // equal total metric is ambiguous
    [InlineData(1u, 10u, true)]
    [InlineData(100u, 0u, true)]
    public void Default_route_comparison_uses_route_plus_interface_metric(uint routeMetric, uint interfaceMetric, bool ready)
    {
        var other = Adapter(7, "Ethernet", "Physical", 6, metric: interfaceMetric);
        var routes = Routes().Concat([new TunDefaultRoute(7, false, Metric: routeMetric)]).ToArray();
        Assert.Equal(ready, TunTakeover.GetError([Tun(), other], routes) is null);
    }

    private static TunDefaultRoute[] Routes() => [new(40, false), new(40, true)];
    private static NetworkAdapterInfo Tun() => Adapter(40, TunTakeover.InterfaceName, "Owned TUN", 53, metric: 10);
    private static NetworkAdapterInfo Adapter(int index, string name, string description, uint type, bool up = true, uint metric = 0) => new()
    {
        Identity = new(Guid.NewGuid(), name), Description = description, Luid = (ulong)index,
        IfIndex = index, Ipv6IfIndex = index, IsUp = up, InterfaceType = type,
        Ipv4Metric = metric, Ipv6Metric = metric,
        Addresses = index == 40 ? [IPAddress.Parse("172.19.0.1"), IPAddress.Parse("fdfe:dcba:9876::1")] : [IPAddress.Parse("192.168.1.2")],
        Gateways = [], DnsServers = []
    };
    private sealed class AliasResolver : IExecutablePathCanonicalizer
    {
        public string? Canonicalize(string path) => path.StartsWith(@"C:\Link", StringComparison.OrdinalIgnoreCase)
            ? @"D:\Real" + path[7..] : null;
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
