using EgressController.Desktop.Services;
using EgressController.Desktop.ViewModels;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Client.Tests;

public sealed class StartupSequenceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("bootstrap")]
    [InlineData("rules")]
    [InlineData("apply")]
    [InlineData("cancel")]
    public async Task ConnectionStartsTunBeforeRulesAndAlwaysEndsItsPreparation(string failure)
    {
        var backend = new Backend(failure); var shell = new ShellViewModel(backend);
        await shell.InitializeAsync(); Assert.Empty(backend.Calls);
        try { await shell.ApplyPreviewAsync(); } catch (IOException) { } catch (OperationCanceledException) { }
        Assert.Equal("bootstrap", backend.Calls[0]);
        if (failure == "bootstrap") Assert.DoesNotContain("rules", backend.Calls);
        else Assert.Equal("rules", backend.Calls[1]);
        Assert.Equal(failure.Length == 0 ? "finish" : "restore", backend.Calls[^1]);
        if (failure == "bootstrap") Assert.Contains("端口已被占用", shell.ConnectionAttempt.Detail);
        if (failure == "cancel") Assert.Contains("取消", shell.ConnectionAttempt.Title);
        Assert.Single(shell.Document.Applications[0].ExecutablePaths);
    }

    [Fact]
    public async Task AppAndNodeSearchOnlyFilterPresentationAndLocalImportNeedsNoNetwork()
    {
        var backend = new Backend(""); var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        string saved = PolicyYaml.Write(shell.Document);
        shell.AppQuery = "com.apple.Safari"; Assert.Equal("Safari", Assert.Single(shell.VisibleApps).Name);
        shell.AppQuery = "Contents/MacOS/Safari"; Assert.Single(shell.VisibleApps);
        shell.NodeQuery = "selected group"; Assert.Single(shell.VisibleNodes);
        shell.NodeQuery = "no-such-node"; Assert.Empty(shell.VisibleNodes); shell.NodeQuery = "VLESS"; Assert.Single(shell.VisibleNodes);
        Assert.Equal(saved, PolicyYaml.Write(shell.Document)); Assert.Empty(backend.Calls);
        shell.LocalSubscriptionYaml = "proxies:\n  - name: Local node\n    type: socks5\n    server: local-node.example.invalid\n    port: 1080\n";
        shell.ImportLocalSubscription();
        Assert.Contains(shell.Document.Nodes, node => node.Name == "Local node"); Assert.Empty(backend.Calls);
        var group = Assert.Single(shell.Document.Groups, item => item.Id == "local-bootstrap-group"); Assert.Null(group.SelectedMemberId); Assert.Equal(GroupKind.Select, group.Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OfflinePhysicalLinksDoNotPreventTunOrApplyingCachedPolicy(bool homeOnline, bool nonHomeOnline)
    {
        var backend = new Backend("") { HomeOnline = homeOnline, NonHomeOnline = nonHomeOnline };
        var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        Assert.Equal(2, shell.CompileOptions().Interfaces.Count);
        await shell.ApplyPreviewAsync();
        Assert.Equal(new[] { "bootstrap", "rules", "apply", "finish" }, backend.Calls);
        Assert.True(shell.PreviewRunning); Assert.False(shell.ConnectionAttempt.HasFailed);
        Assert.Contains("en0", shell.GeneratedConfig); Assert.Contains("en5", shell.GeneratedConfig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticGroupNetworkWorkRunsAfterTunAndErrorsBelongToResources(bool failProbe)
    {
        var backend = new Backend("") { AutomaticGroup = true, FailProbe = failProbe, NonHomeOnline = false };
        var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        try { await shell.ApplyPreviewAsync(); } catch (IOException) { }
        Assert.Equal(new[] { "bootstrap", "probes" }, backend.Calls.Take(2));
        Assert.Equal(failProbe ? "restore" : "finish", backend.Calls[^1]);
        Assert.Equal("完成", shell.ConnectionAttempt.Steps[3].State);
        if (failProbe) Assert.Equal("需处理", shell.ConnectionAttempt.Steps[4].State);
        else Assert.False(shell.ConnectionAttempt.HasFailed);
    }

    private sealed class Backend(string failure) : IClientBackend
    {
        public List<string> Calls { get; } = [];
        public bool IsPreview => false;
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public string LogDirectory => "/synthetic/logs";
        private bool _running;
        public bool HomeOnline { get; init; } = true;
        public bool NonHomeOnline { get; init; } = true;
        public bool AutomaticGroup { get; init; }
        public bool FailProbe { get; init; }
        public Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default)
        {
            var document = new MacPolicyDocument
            {
                Interfaces = new() { HomeId = "home", NonHomeId = "other" }, Defaults = new() { Global = ExitChoice.Proxy("group") },
                Groups = [new() { Id = "group", Name = "Selected group", Kind = GroupKind.Select, MemberIds = ["node"], SelectedMemberId = "node" }],
                Nodes = [new() { Id = "node", Name = "Node", Server = "proxy.example.invalid", Protocol = NodeProtocol.Vless, Tls = true, Port = 443, UserId = "11111111-1111-4111-8111-111111111111" }],
                Applications = [new() { Id = "safari", Name = "Safari", BundleIdentifier = "com.apple.Safari", BundlePath = "/Applications/Safari.app", PrimaryExecutable = "/Applications/Safari.app/Contents/MacOS/Safari", ExecutablePaths = ["/Applications/Safari.app/Contents/MacOS/Safari"], Via = ExitChoice.Home }]
            };
            if (AutomaticGroup) { document.Groups[0].Kind = GroupKind.UrlTest; document.Groups[0].TestUrl = "https://probe.example.invalid/"; }
            return Task.FromResult(new ClientBootstrap(document, [new("home", "en0", "Ethernet", "", HomeOnline), new("other", "en5", "Wi-Fi", "", NonHomeOnline)], [], true, new(false, true, true, "Stopped")));
        }
        public Task BeginConnectionAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
        { Calls.Add("bootstrap"); if (failure == "bootstrap") throw new RuntimeFailureException("proxy.port-in-use"); return Task.CompletedTask; }
        public Task<IReadOnlyList<GroupProbe>> PrepareRequiredProbesAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add("probes"); if (FailProbe) throw new IOException("Synthetic outbound failure");
            return Task.FromResult<IReadOnlyList<GroupProbe>>([new("node", true, 20, Now, "https://probe.example.invalid/")]);
        }
        public Task EndConnectionAsync(bool applied) { Calls.Add(applied ? "finish" : "restore"); return Task.CompletedTask; }
        public Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
        {
            Calls.Add("rules"); if (failure == "rules") throw new IOException(); if (failure == "cancel") throw new OperationCanceledException();
            var bundle = GeoRuleCompiler.Compile([new() { Tag = "cn", Domains = [new(DomainMatchKind.Suffix, "example.invalid")], IpCidrs = ["192.0.2.0/24"] }]);
            return Task.FromResult(new ClientPreparedResources([], new Dictionary<string, byte[]> { ["geosite.dat"] = bundle.GeoSite, ["geoip.dat"] = bundle.GeoIp }, new Dictionary<string, GeoRuleSet>()));
        }
        public Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default)
        { Calls.Add("apply"); if (failure == "apply") return Task.FromResult(new ApplyResult(ApplyDisposition.Failed, null, [new("fixture", "Synthetic failure")])); _running = true; return Task.FromResult(new ApplyResult(ApplyDisposition.Applied, "fixture", [])); }
        public Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ClientRuntimeState(_running, true, true, "Synthetic"));
        public Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClientApplication>>([]);
        public Task<ClientApplication> BuildInventoryAsync(ClientApplication application, IReadOnlyList<string> savedPaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> LoadIconAsync(string bundlePath, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClientRuleCatalogEntry>>([]);
        public Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument current, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
