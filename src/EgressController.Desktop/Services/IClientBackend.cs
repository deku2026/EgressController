using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Desktop.Services;

public sealed record ClientInterface(string Id, string BsdName, string Name, string Detail, bool IsActive);
public sealed record ClientApplication(string Id, string Name, string Product, string Category, string BundlePath,
    string? PrimaryExecutable, IReadOnlyList<string> ExecutablePaths, bool InventoryComplete, string? BundleIdentifier = null,
    byte[]? IconPng = null, string? InventoryError = null);
public sealed record ClientBootstrap(MacPolicyDocument Document, IReadOnlyList<ClientInterface> Interfaces,
    IReadOnlyList<ClientApplication> Applications, bool HasSavedPolicy, ClientRuntimeState Runtime);
public sealed record ClientRuntimeState(bool Running, bool Available, bool HelperInstalled, string Message);
public sealed record ClientRuleCatalogEntry(string Id, string Name, string Detail, IReadOnlyList<string> Sources);
public sealed record ClientPreparedResources(IReadOnlyList<RuleSourceBinding> Bindings,
    IReadOnlyDictionary<string, byte[]> Assets, IReadOnlyDictionary<string, GeoRuleSet> Sources);
public sealed record ClientConnectionResources(ClientPreparedResources Resources, IReadOnlyList<GroupProbe> Probes);
public enum ConnectionAttemptStage { Validating, Core, Authorization, Bootstrap, Resources, Connecting, Ready }
public sealed record ClientOperationProgress(ConnectionAttemptStage Stage, string Detail);

/// <summary>Application services are composed at the entrypoint; views never construct production adapters.</summary>
public interface IClientBackend : IAsyncDisposable
{
    bool IsPreview { get; }
    DateTimeOffset Now { get; }
    string LogDirectory { get; }
    void RecordEvent(string kind, string detail) { }
    void RecordFailure(string operation, Exception error) { }
    void SetProgressSink(IProgress<ClientOperationProgress>? progress) { }
    Task<ConnectionWorkspaceSnapshot> ReadConnectionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ConnectionWorkspaceSnapshot([], Now, false, "当前没有连接观测数据。"));
    Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default);
    Task<ClientApplication> BuildInventoryAsync(ClientApplication application, IReadOnlyList<string> savedPaths,
        CancellationToken cancellationToken = default);
    Task<byte[]?> LoadIconAsync(string bundlePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument document,
        CancellationToken cancellationToken = default);
    Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument current,
        CancellationToken cancellationToken = default);
    Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding,
        CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<GroupProbe>> ProbeMembersAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyList<RequiredGroupProbe> requests, CancellationToken cancellationToken = default)
    {
        var probes = await ProbeAsync(document, options.Interfaces.Single(item => item.StableId == document.Interfaces.NonHomeId), cancellationToken);
        return probes.Where(probe => requests.Any(request => request.NodeId == probe.NodeId && request.TestUrl == probe.TestUrl)).ToArray();
    }
    async Task<IReadOnlyList<GroupProbe>> ProbeMembersWithProgressAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyList<RequiredGroupProbe> requests, IProgress<GroupProbe> progress, CancellationToken cancellationToken = default)
    {
        var results = await ProbeMembersAsync(document, options, requests, cancellationToken);
        foreach (var result in results) progress.Report(result);
        return results;
    }
    Task<IReadOnlyList<GroupProbe>> PrepareRequiredProbesAsync(MacPolicyDocument document, XrayCompileOptions options,
        CancellationToken cancellationToken = default) => ProbeAsync(document,
            options.Interfaces.Single(item => item.StableId == document.Interfaces.NonHomeId), cancellationToken);
    Task BeginConnectionAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task PrepareResourceProxyAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
    async Task<ClientConnectionResources> PrepareConnectionResourcesAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
    {
        await PrepareResourceProxyAsync(document, options, cancellationToken);
        return new(await PrepareResourcesAsync(document, cancellationToken), options.Probes);
    }
    Task EndConnectionAsync(bool applied) => Task.CompletedTask;
    Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default);
    Task<ApplyResult> UpdateGroupsAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default) => ApplyAsync(document, options, assets, cancellationToken);
    Task<bool> StopAsync(CancellationToken cancellationToken = default);
    Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default);
    Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default);
    Task<ConfigurationResetResult> ResetConfigurationAsync(MacPolicyDocument current, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("This backend does not support configuration reset.");
    Task<bool> RepairNetworkAsync(CancellationToken cancellationToken = default) => StopAsync(cancellationToken);
    Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default);
}
