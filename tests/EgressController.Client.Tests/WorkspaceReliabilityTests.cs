using EgressController.Desktop.Preview;
using EgressController.Desktop.Services;
using EgressController.Desktop.ViewModels;
using EgressController.Policy;
using EgressController.Runtime;
using EgressController.MacHelper;
namespace EgressController.Client.Tests;

public sealed class WorkspaceReliabilityTests
{
    [Fact]
    public async Task SetupContinuePersistsCheckedApplicationsAndExactInventoryBeforeLeavingStep()
    {
        PreviewEnvironment.Enter(); var backend = new Backend();
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        shell.SetupStep = 3; var app = shell.VisibleCatalog.First(row => row.CanSelect); app.IsSelected = true;
        await shell.NextSetupCommand.ExecuteAsync();
        Assert.Equal(4, shell.SetupStep); Assert.NotNull(backend.Saved);
        var saved = backend.Saved.Applications.Single(item => item.BundlePath == app.Model.BundlePath);
        Assert.NotEmpty(saved.ExecutablePaths);
        await using var reopened = new ShellViewModel(backend); await reopened.InitializeAsync();
        Assert.Equal(saved.ExecutablePaths, reopened.Document.Applications.Single(item => item.Id == saved.Id).ExecutablePaths);
    }
    [Fact]
    public async Task SlowProbeKeepsNavigationEnabledAndRowsStableThroughResults()
    {
        PreviewEnvironment.Enter(); var backend = new Backend();
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        var group = shell.AllGroups.First(); group.Expanded = true;
        var rows = shell.VisibleGroups; var members = group.Members; var first = members[0];
        backend.PauseProbes = true; var testing = shell.TestGroupAsync(group.Id);
        Assert.True(shell.IsProbing); Assert.False(shell.IsBusy); Assert.True(shell.CanEdit);
        shell.PageIndex = 1; Assert.True(shell.AppsVisible); shell.PageIndex = 3;
        backend.Release.TrySetResult(); await testing;
        Assert.Same(rows, shell.VisibleGroups); Assert.Same(members, group.Members); Assert.Same(first, group.Members[0]);
        Assert.False(shell.IsProbing); Assert.True(shell.CanProbe);
    }
    [Fact]
    public async Task SwitchingToAutomaticStrategyDoesNotBlockWorkspaceWhileTesting()
    {
        PreviewEnvironment.Enter(); var backend = new Backend();
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        var group = shell.AllGroups.First(row => row.Model.Kind == GroupKind.Select);
        backend.PauseProbes = true;
        var changing = shell.SetGroupStrategyAsync(group.Id, GroupKind.UrlTest);
        Assert.True(shell.IsProbing); Assert.False(shell.IsBusy); Assert.True(shell.CanEdit);
        backend.Release.TrySetResult(); await changing;
        Assert.Equal(GroupKind.UrlTest, group.Model.Kind); Assert.False(shell.IsProbing);
    }
    [Fact]
    public async Task ClosingCancelsOwnedProbeAndStillRestoresAfterDraftWriteFailure()
    {
        PreviewEnvironment.Enter(); var backend = new Backend { FailSave = true };
        var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        backend.PauseProbes = true; var probe = shell.RunProbesAsync(); Assert.False(probe.IsCompleted);
        await shell.DisposeAsync(); await probe;
        Assert.Equal(1, backend.DisposeCalls); Assert.True(backend.ProbeCanceled);
    }
    [Fact]
    public async Task FailedRestoreIsRetryableAndNeverPretendsTheClientWasDisposed()
    {
        PreviewEnvironment.Enter(); var backend = new Backend { FailDispose = true };
        var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => shell.DisposeAsync().AsTask());
        shell.ReportShutdownFailure(); Assert.True(shell.CanEdit); Assert.True(shell.HasIssue);
        backend.FailDispose = false; await shell.DisposeAsync(); Assert.Equal(2, backend.DisposeCalls);
    }
    [Fact]
    public async Task ProbeForObsoleteConfigurationCannotOverwriteCurrentGroupSelections()
    {
        PreviewEnvironment.Enter(); var backend = new Backend();
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        backend.PauseProbes = true; var probe = shell.RunProbesAsync(); shell.DnsServer = "https://9.9.9.9/dns-query";
        backend.Release.TrySetResult(); await probe;
        Assert.Equal("https://9.9.9.9/dns-query", shell.Document.Dns.HttpsUrl); Assert.Equal(0, backend.Applies);
    }
    [Fact]
    public async Task RemovedItemsAreHiddenButSelectedMissingTargetRemainsVisibleForRepair()
    {
        PreviewEnvironment.Enter(); await using var shell = new ShellViewModel("nodes");
        var group = shell.AllGroups.First(row => row.Model.Kind == GroupKind.Select);
        var node = shell.Document.Nodes.Last(); node.Diagnostics.Add(new("subscription.removed", "Removed", ObjectId: node.Id));
        group.Model.SelectedMemberId = GroupGraph.Direct; group.Expanded = true;
        Assert.DoesNotContain(group.Members, row => row.Id == node.Id);
        group.Model.SelectedMemberId = node.Id; group.Update(group.Model);
        var missing = Assert.Single(group.Members, row => row.Id == node.Id); Assert.False(missing.CanSelect); Assert.Equal("不可用", missing.Latency);
    }
    [Fact]
    public async Task ReleaseRequiresObservedTakeoverRemovalAndReadFailureIsNotSuccess()
    {
        int calls = 0;
        Assert.True(await RecoverySteps.WaitForReleaseAsync(() => ++calls == 3, () => Task.CompletedTask)); Assert.Equal(3, calls);
        Assert.False(await RecoverySteps.WaitForReleaseAsync(() => false, () => Task.CompletedTask));
        Assert.False(await RecoverySteps.WaitForReleaseAsync(() => throw new IOException(), () => Task.CompletedTask));
    }
    private sealed class Backend : IClientBackend
    {
        private readonly FixtureClientBackend _inner = new();
        public MacPolicyDocument? Saved; public bool PauseProbes, ProbeCanceled, FailSave, FailDispose;
        public int DisposeCalls, Applies;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPreview => true; public DateTimeOffset Now => _inner.Now; public string LogDirectory => _inner.LogDirectory;
        public async Task<ClientBootstrap> LoadAsync(CancellationToken token = default)
        { var loaded = await _inner.LoadAsync(token); return Saved is null ? loaded : loaded with { Document = Copy(Saved) }; }
        private static MacPolicyDocument Copy(MacPolicyDocument document) => PolicyYaml.Read(PolicyYaml.Write(document)).Document!;
        public Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken token = default) => _inner.DiscoverApplicationsAsync(token);
        public Task<ClientApplication> BuildInventoryAsync(ClientApplication app, IReadOnlyList<string> paths, CancellationToken token = default) => _inner.BuildInventoryAsync(app, paths, token);
        public Task<byte[]?> LoadIconAsync(string path, CancellationToken token = default) => _inner.LoadIconAsync(path, token);
        public Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument doc, CancellationToken token = default) => _inner.LoadRuleCatalogAsync(doc, token);
        public Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument doc, CancellationToken token = default) => _inner.ImportSubscriptionAsync(url, doc, token);
        public Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument doc, CancellationToken token = default) => _inner.PrepareResourcesAsync(doc, token);
        public Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument doc, InterfaceBinding binding, CancellationToken token = default) => _inner.ProbeAsync(doc, binding, token);
        public async Task<IReadOnlyList<GroupProbe>> ProbeMembersAsync(MacPolicyDocument doc, XrayCompileOptions options, IReadOnlyList<RequiredGroupProbe> requests, CancellationToken token = default)
        {
            try { if (PauseProbes) await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { ProbeCanceled = true; throw; }
            return requests.Select(request => new GroupProbe(request.NodeId, true, 35, Now, request.TestUrl)).ToArray();
        }
        public Task<ApplyResult> ApplyAsync(MacPolicyDocument doc, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken token = default)
        { Applies++; return _inner.ApplyAsync(doc, options, assets, token); }
        public Task<bool> StopAsync(CancellationToken token = default) => _inner.StopAsync(token);
        public Task<ClientRuntimeState> ReadStatusAsync(CancellationToken token = default) => _inner.ReadStatusAsync(token);
        public Task SaveDraftAsync(MacPolicyDocument doc, CancellationToken token = default)
        { if (FailSave) throw new IOException("synthetic write failure"); Saved = Copy(doc); return Task.CompletedTask; }
        public Task OpenLogDirectoryAsync(CancellationToken token = default) => _inner.OpenLogDirectoryAsync(token);
        public ValueTask DisposeAsync() { DisposeCalls++; if (FailDispose) throw new IOException("synthetic recovery failure"); return ValueTask.CompletedTask; }
    }
}
