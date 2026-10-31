using EgressController.Desktop.Services;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Desktop.Production;

/// <summary>Labels observed generations once. Editing the draft cannot relabel historical routes.</summary>
public sealed class ConnectionObservationMapper
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MacPolicyDocument> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionRecord> _labels = new(StringComparer.Ordinal);
    private IReadOnlyList<ConnectionRecord> _last = [];
    private string? _observedGeneration;
    public void RememberApplied(string generation, MacPolicyDocument document)
    {
        var saved = PolicyYaml.Read(PolicyYaml.Write(document)).Document ?? throw new InvalidDataException("Invalid applied observation metadata.");
        lock (_gate)
        {
            if (_applied.ContainsKey(generation)) return;
            _applied[generation] = saved;
            while (_applied.Count > 4) _applied.Remove(_applied.Keys.First(key => key != _observedGeneration && key != generation));
        }
    }
    public ConnectionWorkspaceSnapshot Map(CoreConnectionSnapshot snapshot, DateTimeOffset now)
    { lock (_gate) return MapCore(snapshot, now); }
    private ConnectionWorkspaceSnapshot MapCore(CoreConnectionSnapshot snapshot, DateTimeOffset now)
    {
        if (!CoreConnectionSnapshots.IsValid(snapshot, now)) return new(_last, now, false, "连接观测已过期或无法读取；历史记录不代表当前存活连接。");
        _observedGeneration = snapshot.GenerationId ?? _observedGeneration;
        _applied.TryGetValue(snapshot.GenerationId ?? "", out var policy);
        var records = new List<ConnectionRecord>();
        foreach (var row in snapshot.Records)
        {
            string key = (snapshot.GenerationId ?? "unknown") + ":" + row.Id;
            _labels.TryGetValue(key, out var labels);
            var app = policy?.Applications.FirstOrDefault(item => "app-" + item.Id == row.MatchedRule);
            var rule = policy?.RuleSets.FirstOrDefault(item => ("domains-" + item.Id == row.MatchedRule || "ips-" + item.Id == row.MatchedRule));
            string? target = row.OutboundTag switch { "home" => "家宽直连", "nonhome" => "非家宽直连", null => null,
                _ => policy?.Nodes.FirstOrDefault(item => item.Id == row.OutboundTag)?.Name ?? row.OutboundTag };
            var record = new ConnectionRecord(key, row.LastObservedAt)
            {
                ApplicationId = labels?.ApplicationId ?? app?.Id, ApplicationName = labels?.ApplicationName ?? app?.Name,
                ProcessId = row.ProcessId, ExecutablePath = row.ExecutablePath,
                Transport = row.Transport, Host = row.Host, RemoteIp = row.RemoteIp, RemotePort = row.RemotePort,
                Activity = row.Activity switch { CoreConnectionActivity.Active => ConnectionActivity.Active, CoreConnectionActivity.Closed => ConnectionActivity.Closed, _ => ConnectionActivity.Unknown },
                ObservedExitKind = labels?.ObservedExitKind ?? (row.OutboundTag switch
                { "home" => ExitKind.Home, "nonhome" => ExitKind.NonHome,
                    { } tag when policy?.Nodes.Any(item => item.Id == tag) == true => ExitKind.Proxy, _ => (ExitKind?)null }),
                ObservedTargetName = labels?.ObservedTargetName ?? target,
                MatchedRule = labels?.MatchedRule ?? (app is not null ? "应用 · " + app.Name : rule is not null ? "规则 · " + rule.Name : row.MatchedRule),
                UploadedBytes = row.UploadedBytes, DownloadedBytes = row.DownloadedBytes
            };
            _labels[key] = record; records.Add(record);
        }
        if (records.Count > 0 || snapshot.IsAvailable) _last = records;
        var retained = _last.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _labels.Keys.Where(key => !retained.Contains(key)).ToArray()) _labels.Remove(key);
        string? limitation = snapshot.Limitation;
        if (snapshot.DroppedRecords > 0) limitation = $"保留最近 {CoreConnectionSnapshots.MaximumRecords} 条，已有 {snapshot.DroppedRecords} 条超出内存范围。 " + limitation;
        return new(_last, snapshot.CapturedAt, snapshot.IsAvailable, limitation);
    }
}
