using EgressController.Policy;

namespace EgressController.Desktop.Services;

/// <summary>Activity is authoritative observation, never inferred from a missing close event.</summary>
public enum ConnectionActivity { Unknown, Active, Closed }

/// <summary>Observed facts for one stable connection. Null means the observer did not supply that fact.</summary>
public sealed record ConnectionRecord(string Id, DateTimeOffset LastObservedAt)
{
    public string? ApplicationId { get; init; }
    public string? ApplicationName { get; init; }
    public string? ExecutablePath { get; init; }
    public int? ProcessId { get; init; }
    public string? Transport { get; init; }
    public string? Host { get; init; }
    public string? RemoteIp { get; init; }
    public int? RemotePort { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public ConnectionActivity Activity { get; init; } = ConnectionActivity.Unknown;
    public ExitKind? ObservedExitKind { get; init; }
    public string? ObservedTargetName { get; init; }
    public string? ObservedInterface { get; init; }
    public string? MatchedRule { get; init; }
    public long? UploadedBytes { get; init; }
    public long? DownloadedBytes { get; init; }
    public double? UploadBytesPerSecond { get; init; }
    public double? DownloadBytesPerSecond { get; init; }
}

/// <summary>Whole bounded observer snapshot. Replacing a snapshot does not mutate policy or runtime state.</summary>
public sealed record ConnectionWorkspaceSnapshot(IReadOnlyList<ConnectionRecord> Records,
    DateTimeOffset CapturedAt, bool IsAvailable = true, string? Limitation = null);
