// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace EgressController.Runtime;

public enum CoreConnectionActivity { Unknown, Active, Closed }

/// <summary>Observed facts only. Missing activity, identity and counters are never guessed from access logs.</summary>
public sealed record CoreConnectionRecord(string Id, DateTimeOffset LastObservedAt)
{
    public string? GenerationId { get; init; }
    public string? SourceIp { get; init; }
    public int? SourcePort { get; init; }
    public string? Transport { get; init; }
    public string? Host { get; init; }
    public string? RemoteIp { get; init; }
    public int? RemotePort { get; init; }
    public string? OutboundTag { get; init; }
    public string? MatchedRule { get; init; }
    public int? ProcessId { get; init; }
    public string? ExecutablePath { get; init; }
    public long? UploadedBytes { get; init; }
    public long? DownloadedBytes { get; init; }
    public CoreConnectionActivity Activity { get; init; } = CoreConnectionActivity.Unknown;
}

public sealed record CoreConnectionSnapshot(IReadOnlyList<CoreConnectionRecord> Records, DateTimeOffset CapturedAt,
    bool IsAvailable = true, string? Limitation = null, long DroppedRecords = 0)
{
    public string? GenerationId { get; init; }
}

/// <summary>Bounds the optional observation payload independently of authenticated lifecycle status.</summary>
public static class CoreConnectionSnapshots
{
    public const int MaximumRecords = 128;
    public const string StockCoreLimitation = "Xray 访问记录不提供连接存活、逐连接字节及完整进程身份；缺失字段保持未知，记录可能因核心日志限流而不完整。";
    public static CoreConnectionSnapshot Unavailable(DateTimeOffset now, string? reason = null)
        => new([], now, false, reason ?? "连接观测暂不可用。请检查核心状态。");

    public static bool IsValid(CoreConnectionSnapshot? value, DateTimeOffset now)
    {
        if (value is null || value.Records is null || value.Records.Count > MaximumRecords || value.DroppedRecords < 0
            || value.CapturedAt < now.AddSeconds(-15) || value.CapturedAt > now.AddSeconds(5)
            || !SafeText(value.Limitation, 256) || !SafeText(value.GenerationId, 64)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in value.Records)
            if (row is null || string.IsNullOrEmpty(row.Id) || !SafeText(row.Id, 160) || !ids.Add(row.Id)
                || row.LastObservedAt > value.CapturedAt.AddSeconds(5) || row.LastObservedAt < DateTimeOffset.UnixEpoch
                || !SafeText(row.GenerationId, 64) || !Enum.IsDefined(row.Activity) || row.Transport is not (null or "tcp" or "udp")
                || !IpOrNull(row.SourceIp) || !IpOrNull(row.RemoteIp) || !PortOrNull(row.SourcePort) || !PortOrNull(row.RemotePort)
                || !SafeText(row.Host, 253) || row.Host is { } host && Uri.CheckHostName(host) == UriHostNameType.Unknown
                || !SafeTag(row.OutboundTag) || !SafeTag(row.MatchedRule) || row.ProcessId is <= 0
                || !SafeText(row.ExecutablePath, 4096) || row.ExecutablePath is { } path && (!path.StartsWith('/') || path.Contains("/../", StringComparison.Ordinal))
                || row.UploadedBytes is < 0 || row.DownloadedBytes is < 0
                || row.Id.Length + (row.SourceIp?.Length ?? 0) + (row.Transport?.Length ?? 0) + (row.Host?.Length ?? 0)
                    + (row.RemoteIp?.Length ?? 0) + (row.OutboundTag?.Length ?? 0) + (row.MatchedRule?.Length ?? 0)
                    + (row.ExecutablePath?.Length ?? 0) > 768) return false; // Escaped JSON remains below the 1MiB IPC frame cap.
        return true;
    }

    internal static bool SafeText(string? value, int maximum) => value is null || value.Length <= maximum && !value.Any(char.IsControl);
    internal static bool SafeTag(string? value) => value is null || value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    private static bool IpOrNull(string? value) => value is null || value.Length <= 45 && IPAddress.TryParse(value, out _);
    private static bool PortOrNull(int? value) => value is null or > 0 and <= 65535;
}

/// <summary>
/// Parses only the pinned Xray TUN/access/dispatcher grammar. Raw lines are discarded, never exposed or persisted.
/// This is a lossy observational history, not an authoritative active-connection list or traffic ledger input.
/// </summary>
public sealed partial class XrayConnectionObserver
{
    public const int MaximumLineCharacters = 4096;
    private readonly object _gate = new();
    private string _generation;
    private readonly int _capacity;
    private readonly List<CoreConnectionRecord> _records = [];
    private readonly Dictionary<uint, string> _sessions = [];
    private long _sequence, _dropped;
    private bool _available = true;

    public XrayConnectionObserver(string generationId, int capacity = CoreConnectionSnapshots.MaximumRecords)
    {
        if (generationId is not { Length: 64 } || !generationId.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid observer generation.", nameof(generationId));
        _generation = generationId;
        _capacity = Math.Clamp(capacity, 1, CoreConnectionSnapshots.MaximumRecords);
    }

    public void SetGeneration(string generation)
    {
        if (generation.Length != 64 || !generation.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid generation.");
        lock (_gate) _generation = generation;
    }
    public void ObserveLine(string? line, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            if (line is null || line.Length > MaximumLineCharacters) { Drop(); return; }
            if (observedAt < DateTimeOffset.UnixEpoch || line.Any(character => char.IsControl(character))) return;
            var info = InformationLine().Match(line);
            if (info.Success && uint.TryParse(info.Groups["session"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint session) && session != 0)
            {
                string module = info.Groups["module"].Value, message = info.Groups["message"].Value;
                if (module == "proxy/tun") ObserveTun(session, message, observedAt);
                else if (module == "app/dispatcher") ObserveRoute(session, message, observedAt);
                return;
            }
            var access = AccessLine().Match(line);
            if (access.Success && TryEndpoint(access.Groups["source"].Value, out var source) && source.Ip is not null
                && TryEndpoint(access.Groups["target"].Value, out var target) && source.Transport == target.Transport)
            {
                string outbound = access.Groups["outbound"].Value;
                var matches = _records.Where(row => MatchesTuple(row, source, target) && (row.OutboundTag is null || row.OutboundTag == outbound)).Take(2).ToArray();
                if (matches.Length == 1) Replace(matches[0] with { OutboundTag = outbound, LastObservedAt = Latest(matches[0].LastObservedAt, observedAt) });
                else Add(FromEndpoints(NextId("access"), source, target, observedAt) with { OutboundTag = outbound });
            }
        }
    }

    public void MarkUnavailable() { lock (_gate) _available = false; }
    public void RecordDroppedLine() { lock (_gate) Drop(); }
    public CoreConnectionSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate) return new(_records.OrderByDescending(row => row.LastObservedAt).ToArray(), now, _available,
            _available ? CoreConnectionSnapshots.StockCoreLimitation : "核心输出读取中断；已有观测仅供历史查看。", _dropped) { GenerationId = _generation };
    }

    private void ObserveTun(uint session, string message, DateTimeOffset now)
    {
        var processing = Processing().Match(message);
        if (!processing.Success || !TryEndpoint(processing.Groups["source"].Value, out var source) || source.Ip is null
            || !TryEndpoint(processing.Groups["target"].Value, out var target) || source.Transport != target.Transport) return;
        // A new processing event receives its own identity even if the core's random 32-bit session ID collided.
        var record = FromEndpoints(NextId(session.ToString(CultureInfo.InvariantCulture)), source, target, now);
        Add(record); _sessions[session] = record.Id;
    }
    private void ObserveRoute(uint session, string message, DateTimeOffset now)
    {
        var route = Routed().Match(message);
        if (!route.Success || !TryEndpoint(route.Groups["target"].Value, out var target)) return;
        string outbound = route.Groups["outbound"].Value;
        string? rule = route.Groups["rule"].Success ? route.Groups["rule"].Value : null;
        var current = _sessions.TryGetValue(session, out var id) ? _records.Find(row => row.Id == id) : null;
        if (current is not null && (current.Transport != target.Transport || current.RemotePort != target.Port
            || current.RemoteIp is not null && target.Ip is not null && current.RemoteIp != target.Ip)) return;
        if (current is null)
        {
            current = new(NextId(session.ToString(CultureInfo.InvariantCulture)), now) { Transport = target.Transport, Host = target.Host, RemoteIp = target.Ip, RemotePort = target.Port };
            Add(current); _sessions[session] = current.Id;
        }
        Replace(current with { Host = target.Host ?? current.Host, OutboundTag = outbound, MatchedRule = rule,
            LastObservedAt = Latest(current.LastObservedAt, now) });
    }
    private string NextId(string kind) => _generation + ":" + kind + ":" + (++_sequence).ToString(CultureInfo.InvariantCulture);
    private void Add(CoreConnectionRecord record)
    {
        if (_records.Count == _capacity)
        {
            string removed = _records[0].Id; _records.RemoveAt(0);
            foreach (uint key in _sessions.Where(pair => pair.Value == removed).Select(pair => pair.Key).ToArray()) _sessions.Remove(key);
            Drop();
        }
        _records.Add(record with { GenerationId = record.GenerationId ?? _generation });
    }
    private void Replace(CoreConnectionRecord record) { int index = _records.FindIndex(row => row.Id == record.Id); if (index >= 0) _records[index] = record with { GenerationId = record.GenerationId ?? _records[index].GenerationId }; }
    private void Drop() { if (_dropped < long.MaxValue) _dropped++; }
    private static DateTimeOffset Latest(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static CoreConnectionRecord FromEndpoints(string id, Endpoint source, Endpoint target, DateTimeOffset now) => new(id, now)
    { SourceIp = source.Ip, SourcePort = source.Port, Transport = target.Transport, Host = target.Host, RemoteIp = target.Ip, RemotePort = target.Port };
    private static bool MatchesTuple(CoreConnectionRecord row, Endpoint source, Endpoint target) => row.SourceIp == source.Ip && row.SourcePort == source.Port
        && row.Transport == target.Transport && row.RemotePort == target.Port && (target.Ip is not null ? row.RemoteIp == target.Ip : row.Host == target.Host);

    // Xray Destination.NetAddr does not bracket IPv6; split at the final colon and validate the address.
    private static bool TryEndpoint(string text, out Endpoint endpoint)
    {
        endpoint = default;
        if (text.Length is < 7 or > 320 || !(text.StartsWith("tcp:", StringComparison.Ordinal) || text.StartsWith("udp:", StringComparison.Ordinal))) return false;
        int delimiter = text.LastIndexOf(':');
        if (delimiter <= 4 || !int.TryParse(text.AsSpan(delimiter + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is <= 0 or > 65535) return false;
        string host = text[4..delimiter].Trim('[', ']');
        if (IPAddress.TryParse(host, out var address)) endpoint = new(text[..3], address.ToString(), null, port);
        else
        {
            if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns || host.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))) return false;
            endpoint = new(text[..3], null, host, port);
        }
        return true;
    }
    private readonly record struct Endpoint(string Transport, string? Ip, string? Host, int Port);
    [GeneratedRegex(@"^(?:[0-9]{4}/[0-9]{2}/[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})? )?\[Info\] \[(?<session>[0-9]{1,10})\] (?<module>proxy/tun|app/dispatcher): (?<message>.+)$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex InformationLine();
    [GeneratedRegex(@"^processing from (?<source>\S+) to (?<target>\S+)$", RegexOptions.CultureInvariant, 100)] private static partial Regex Processing();
    [GeneratedRegex(@"^(?:Hit route rule: \[(?<rule>[A-Za-z0-9_.-]{1,128})\] so )?taking detour \[(?<outbound>[A-Za-z0-9_.-]{1,128})\] for \[(?<target>[^\s]+)\]$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Routed();
    [GeneratedRegex(@"^(?:[0-9]{4}/[0-9]{2}/[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})? )?from (?<source>\S+) accepted (?<target>\S+) \[tun (?:->|>>|==>) (?<outbound>[A-Za-z0-9_.-]{1,128})\](?: .*)?$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AccessLine();
}
