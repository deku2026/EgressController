using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Diagnostics;
using EgressController.Guardian;
using EgressController.Rules.Catalog;
using EgressController.State.Traffic;
using EgressController.Windows.Process;

namespace EgressController.App;

/// <summary>UI boundary. The preview implements this without constructing any production service.</summary>
public interface IAppController
{
    Services.ApplicationVisualCatalog Visuals { get; }
    bool IsPreview { get; }
    bool IsTunRunning { get; }
    bool IsUpdatingProfile { get; }
    ProtectionReadiness Protection { get; }
    string TunStatus { get; }
    string ProtectionStatus { get; }
    string LastTunFailure { get; }
    string LastMessage { get; }
    string DownloadStatus { get; }
    IReadOnlyList<string> DiscoveryNotices { get; }
    string UpstreamSummary { get; }
    EgressProfileDocument Profile { get; }
    IReadOnlyList<NetworkAdapterInfo> Adapters { get; }
    ProxyPortSnapshot ProxyBindings { get; }
    GuardManagementViewModel Guard { get; }
    string GuardianConnectionStatus { get; }
    int? DirectDelayMilliseconds { get; }
    int? ProxyDelayMilliseconds { get; }
    TrafficSnapshot TrafficSnapshot { get; }
    string DirectInternetStatus { get; }
    string ProxyInternetStatus { get; }
    string NetworkMonitorStatus { get; }
    DateTimeOffset? NetworkLastCheckedAtUtc { get; }
    string DiagnosticsStatus { get; }
    ConnectionHistoryStore ConnectionHistory { get; }
    DateTimeOffset? ConnectionsUpdatedAtUtc { get; }
    DateTimeOffset? TrafficUpdatedAtUtc { get; }
    long TrafficUp { get; }
    long TrafficDown { get; }
    TrafficUsageSnapshot TrafficUsage { get; }
    string TrafficUsageError { get; }
    DiagnosticJournal Diagnostics { get; }
    BoundedLogStore Logs { get; }
    string LogPath { get; }
    IReadOnlyList<ProtectionEvent> ProtectionEvents { get; }
    SingBoxRuleCatalog? Catalog { get; }
    string CatalogDirectory { get; }
    string CatalogCommit { get; }
    IReadOnlyList<string> SelectedRuleNames { get; }
    IReadOnlyList<string> ManualDomains { get; }
    IReadOnlyList<NetworkAdapterInfo> RefreshAdapters();
    IReadOnlyList<LaunchTarget> ScanTargets();
    IReadOnlyList<LaunchTarget> CachedTargets { get; }
    LaunchTarget? FindApplication(string key);
    IReadOnlyDictionary<string, string> ApplicationCategories { get; }
    Task<ControllerOperationResult> SetApplicationCategoryAsync(LaunchTarget target, string? category);
    Task<ControllerOperationResult> RefreshApplicationInventoryAsync(LaunchTarget target);
    Task<ControllerOperationResult> AddManualApplicationAsync(string path, string? replaceKey = null);
    Task<ControllerOperationResult> SetAdapterRolesAsync(string? directId, string? proxyId);
    Task<ControllerOperationResult> SetApplicationsRouteAsync(IEnumerable<LaunchTarget> targets, bool enabled,
        EgressRouteTarget? route = null, IReadOnlyDictionary<string, EgressRouteTarget>? routes = null, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> SetRuleSetAsync(string name, bool enabled, EgressRouteTarget? route = null, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> SetRuleSetsAsync(IEnumerable<string> names, bool enabled, EgressRouteTarget? route = null,
        IReadOnlyDictionary<string, EgressRouteTarget>? routes = null, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> AddManualDomainAsync(string domain, EgressRouteTarget? route = null, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> RemoveManualDomainAsync(string domain, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> AddUpstreamPortAsync(int port, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> RemoveUpstreamPortAsync(int port, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> SetDefaultUpstreamPortAsync(int port, CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> CheckInternetHealthAsync(CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> CloseAllConnectionsAsync(CancellationToken cancellationToken = default);
    Task<ControllerOperationResult> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<SingBoxCatalogUpdateResult> RefreshCatalogAsync(CancellationToken cancellationToken = default);
    void ClearConnectionHistory();
    void ClearProtectionHistory();
    ControllerOperationResult ReportFailure(string stage, string code, string message, Exception? error = null);
}
