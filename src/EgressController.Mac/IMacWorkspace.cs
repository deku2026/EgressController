using EgressController.Core.Routing;
using EgressController.Surge;

namespace EgressController.Mac;

public sealed record WorkspaceSnapshot(RoutingProfile Profile, MacSettings Settings, IReadOnlyList<ResolvedAdapter> Adapters, IReadOnlyList<MacApplication> Applications);
public interface IMacWorkspace
{
    bool IsPreview { get; }
    string DirectoryPath { get; }
    Task<WorkspaceSnapshot> LoadAsync(CancellationToken token);
    Task<IReadOnlyList<ResolvedAdapter>> RefreshAdaptersAsync(CancellationToken token);
    Task<IReadOnlyList<MacApplication>> RefreshApplicationsAsync(CancellationToken token);
    Task<MacApplication> InspectApplicationAsync(string path, CancellationToken token);
    Task<SocksStatus> ProbeAsync(int port, CancellationToken token);
    Task SaveAsync(RoutingProfile profile, CancellationToken token);
    Task<PreparedConfiguration> PrepareAsync(RoutingProfile profile, CancellationToken token);
    Task<string> RestoreAsync(CancellationToken token);
    Task<SurgeStatus> CheckSurgeAsync(MacSettings settings, string? key, CancellationToken token);
    Task SaveSettingsAsync(MacSettings settings, string? surgeKey, CancellationToken token);
    Task<IReadOnlyList<SurgeRule>> RulesAsync(bool refresh, CancellationToken token);
    Task<EgressController.Subscriptions.SurgeSource?> ReadSubscriptionAsync(CancellationToken token);
    Task OpenLogsAsync();
}
