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

/// <summary>Application services are composed at the entrypoint; views never construct production adapters.</summary>
public interface IClientBackend : IAsyncDisposable
{
    bool IsPreview { get; }
    DateTimeOffset Now { get; }
    string LogDirectory { get; }
    Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default);
    Task<ClientApplication> BuildInventoryAsync(ClientApplication application, IReadOnlyList<string> savedPaths,
        CancellationToken cancellationToken = default);
    Task<byte[]?> LoadIconAsync(string bundlePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(InterfaceBinding binding, MacPolicyDocument document,
        CancellationToken cancellationToken = default);
    Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument current, InterfaceBinding binding,
        CancellationToken cancellationToken = default);
    Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document, InterfaceBinding binding,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding,
        CancellationToken cancellationToken = default);
    Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default);
    Task<bool> StopAsync(CancellationToken cancellationToken = default);
    Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default);
    Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default);
    Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default);
}
