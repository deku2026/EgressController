using EgressController.Desktop.Preview;
using EgressController.Desktop.Services;
using EgressController.Desktop.ViewModels;
using EgressController.Policy;
using EgressController.Runtime;
namespace EgressController.Client.Tests;

public sealed class GroupWorkspaceTests
{
    [Fact]
    public async Task SelectionChangesOnlyRoutingAndRetainedChoicesSurviveRefresh()
    {
        PreviewEnvironment.Enter(); await using var shell = new ShellViewModel("nodes");
        await shell.ApplyPreviewAsync(); int starts = shell.Core.RestartCount;
        var manual = shell.AllGroups.Single(group => group.Name == "手动选择");
        string[][] inventories = shell.Document.Applications.Select(app => app.ExecutablePaths.ToArray()).ToArray();
        await shell.SelectGroupMemberAsync(manual.Id, GroupGraph.Direct);
        Assert.Equal(GroupGraph.Direct, manual.Model.SelectedMemberId); Assert.Equal(starts, shell.Core.RestartCount);
        var removable = shell.AllGroups.Single(group => group.Name == "Claude"); removable.Retained = false;
        Assert.DoesNotContain(shell.GlobalChoices, choice => choice.Id == removable.Id);
        Assert.DoesNotContain(shell.VisibleGroups, group => group.Id == removable.Id);
        await shell.ImportSubscriptionAsync();
        Assert.False(shell.AllGroups.Single(group => group.Id == removable.Id).Retained);
        Assert.Equal(GroupGraph.Direct, shell.AllGroups.Single(group => group.Id == manual.Id).Model.SelectedMemberId);
        Assert.Equal(inventories.SelectMany(paths => paths), shell.Document.Applications.SelectMany(app => app.ExecutablePaths));
    }
    [Fact]
    public async Task AutomaticIntervalUsesAppliedSnapshotAndNeverAppliesPendingDnsDraft()
    {
        PreviewEnvironment.Enter(); var backend = new ClockBackend(); await using var shell = new ShellViewModel(backend);
        await shell.InitializeAsync(); await shell.ApplyPreviewAsync(); int starts = backend.Inner.Core.RestartCount; int initialTests = backend.GroupTests;
        string appliedDns = shell.Document.Dns.UpstreamIp; backend.PreferTokyo = true;
        shell.DnsServer = "9.9.9.9"; backend.Advance(299); await shell.MaintainAutomaticGroupsAsync(); Assert.Equal(initialTests, backend.GroupTests);
        backend.Advance(1); await shell.MaintainAutomaticGroupsAsync(); Assert.Equal(initialTests + 1, backend.GroupTests);
        Assert.Equal(starts, backend.Inner.Core.RestartCount); Assert.True(backend.Inner.Core.RouteUpdateCount > 0);
        Assert.Equal("9.9.9.9", shell.Document.Dns.UpstreamIp); Assert.True(shell.HasChanges);
        Assert.Equal(appliedDns, PolicyYaml.Read(backend.Inner.Core.Current!.PolicyYaml).Document!.Dns.UpstreamIp);
        int applies = backend.Inner.Core.ApplyCount; backend.FailProbes = true; backend.Advance(300); await shell.MaintainAutomaticGroupsAsync();
        Assert.Equal(applies, backend.Inner.Core.ApplyCount); Assert.True(shell.PreviewRunning); Assert.False(shell.HasIssue);
    }
    [Fact]
    public async Task ExplicitMeasurementAppliesAutomaticSelectionAndUnavailableResultsKeepCommittedLabel()
    {
        PreviewEnvironment.Enter(); var backend = new ClockBackend(); await using var shell = new ShellViewModel(backend);
        await shell.InitializeAsync(); await shell.ApplyPreviewAsync(); int starts = backend.Inner.Core.RestartCount;
        var group = shell.AllGroups.Single(row => row.Model.Kind == GroupKind.UrlTest);
        backend.PreferTokyo = true; await shell.TestGroupAsync(group.Id);
        string selected = shell.Document.Nodes.Single(node => node.Name == "东京 · 01").Id;
        Assert.Equal(selected, shell.ResolveGroupMember(group.Model)); Assert.Equal(starts, backend.Inner.Core.RestartCount);
        int applies = backend.Inner.Core.ApplyCount;
        backend.Unavailable = true; await shell.TestGroupAsync(group.Id);
        Assert.Equal(applies, backend.Inner.Core.ApplyCount); Assert.Equal(selected, shell.ResolveGroupMember(group.Model));
        Assert.True(shell.PreviewRunning); Assert.False(shell.HasIssue);
    }
    private sealed class ClockBackend : IClientBackend
    {
        public FixtureClientBackend Inner { get; } = new();
        private DateTimeOffset _now = PreviewFixtures.ObservationTime;
        public bool IsPreview => true;
        public DateTimeOffset Now => _now;
        public string LogDirectory => Inner.LogDirectory;
        public int GroupTests { get; private set; }
        public bool FailProbes { get; set; }
        public bool PreferTokyo { get; set; }
        public bool Unavailable { get; set; }
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
        public Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default) => Inner.LoadAsync(cancellationToken);
        public Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default) => Inner.DiscoverApplicationsAsync(cancellationToken);
        public Task<ClientApplication> BuildInventoryAsync(ClientApplication application, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Inner.BuildInventoryAsync(application, paths, cancellationToken);
        public Task<byte[]?> LoadIconAsync(string path, CancellationToken cancellationToken = default) => Inner.LoadIconAsync(path, cancellationToken);
        public Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Inner.LoadRuleCatalogAsync(document, cancellationToken);
        public Task<MacPolicyDocument> ImportSubscriptionAsync(Uri uri, MacPolicyDocument current, CancellationToken cancellationToken = default) => Inner.ImportSubscriptionAsync(uri, current, cancellationToken);
        public Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Inner.PrepareResourcesAsync(document, cancellationToken);
        public Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding, CancellationToken cancellationToken = default) => Inner.ProbeAsync(document, binding, cancellationToken);
        public Task<IReadOnlyList<GroupProbe>> ProbeMembersAsync(MacPolicyDocument document, XrayCompileOptions options, IReadOnlyList<RequiredGroupProbe> requests, CancellationToken cancellationToken = default)
        {
            GroupTests++; if (FailProbes) throw new IOException("Synthetic outbound probe failure");
            string fastest = document.Nodes.Single(node => node.Name == (PreferTokyo ? "东京 · 01" : "新加坡 · 02")).Id;
            return Task.FromResult<IReadOnlyList<GroupProbe>>(requests.Select(request => new GroupProbe(request.NodeId, !Unavailable, request.NodeId == fastest ? 10 : 200, Now, request.TestUrl)).ToArray());
        }
        public Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default) => Inner.ApplyAsync(document, options, assets, cancellationToken);
        public Task<bool> StopAsync(CancellationToken cancellationToken = default) => Inner.StopAsync(cancellationToken);
        public Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default) => Inner.ReadStatusAsync(cancellationToken);
        public Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Inner.SaveDraftAsync(document, cancellationToken);
        public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default) => Inner.OpenLogDirectoryAsync(cancellationToken);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
