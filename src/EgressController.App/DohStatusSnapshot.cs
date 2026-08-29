using EgressController.SingBox.Configuration;

namespace EgressController.App;

public sealed record DohStatusSnapshot(
    string Tag,
    string RoutePlane,
    string Provider,
    bool IsFallback,
    string Server,
    int ServerPort,
    string Path,
    string ServerName,
    string Detour,
    bool IsAvailable,
    bool IsActive,
    bool? IsHealthy,
    string State,
    string Detail,
    DateTimeOffset? LastCheckedAtUtc,
    long? LatencyMilliseconds)
{
    public static DohStatusSnapshot Create(
        SingBoxDohEndpointDefinition endpoint,
        bool isAvailable,
        bool isActive,
        bool? isHealthy,
        string state,
        string detail = "",
        DateTimeOffset? lastCheckedAtUtc = null,
        long? latencyMilliseconds = null)
        => new(
            endpoint.Tag,
            endpoint.RoutePlaneLabel,
            endpoint.Provider,
            endpoint.IsFallback,
            endpoint.Server,
            endpoint.ServerPort,
            endpoint.Path,
            endpoint.ServerName,
            endpoint.Detour,
            isAvailable,
            isActive,
            isHealthy,
            state,
            detail,
            lastCheckedAtUtc,
            latencyMilliseconds);
}
