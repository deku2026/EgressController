using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace EgressController.Subscriptions;

public enum ProbeOutcome { Success, Timeout, Unknown }
public sealed record NodeObservation(string Id, string Name, ProbeOutcome Outcome, DateTimeOffset At, double? LatencyMs = null);
public sealed record HealthCondition(string Pattern, string Metric, double Threshold);
public sealed record HealthPolicy
{
    public HealthCondition[] Conditions { get; init; } = [new(".*", "timeout-percent", 50)];
    public bool RequireAll { get; init; } = true;
    public int ConsecutiveRounds { get; init; } = 2;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan? MaximumRefreshAge { get; init; }
}
public sealed record HealthState(int ConsecutiveFailures = 0, DateTimeOffset? LastAttempt = null, DateTimeOffset? LastSuccess = null);
public sealed record HealthDecision(bool Refresh, string Reason, HealthState State, int Known, int Unknown);
public static class HealthEvaluator
{
    public static HealthDecision Evaluate(HealthPolicy policy, HealthState prior, IReadOnlyList<NodeObservation> observations, bool backendHealthy, bool networkAvailable, DateTimeOffset now)
    {
        if (policy.ConsecutiveRounds < 1 || policy.Cooldown < TimeSpan.Zero) throw new ArgumentException("健康检测条件无效。");
        var nodes = observations.GroupBy(n => n.Id, StringComparer.Ordinal).Select(g => g.MaxBy(n => n.At)!).ToArray();
        var fresh = nodes.Select(n => now - n.At > TimeSpan.FromMinutes(20) || n.At > now.AddMinutes(1) ? n with { Outcome = ProbeOutcome.Unknown } : n).ToArray();
        int known = fresh.Count(n => n.Outcome != ProbeOutcome.Unknown), unknown = fresh.Length - known;
        HealthDecision Result(bool refresh, string reason, HealthState state) => new(refresh, reason, state, known, unknown);
        if (!backendHealthy || !networkAvailable) return Result(false, "后端或本机网络不可用；暂停订阅刷新", prior with { ConsecutiveFailures = 0 });
        var matches = new List<bool?>();
        foreach (var condition in policy.Conditions)
        {
            var regex = new Regex(condition.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            NodeObservation[] group;
            try { group = fresh.Where(n => regex.IsMatch(n.Name)).ToArray(); }
            catch (RegexMatchTimeoutException) { return Result(false, "分组正则执行超时", prior with { ConsecutiveFailures = 0 }); }
            var tested = group.Where(n => n.Outcome != ProbeOutcome.Unknown).ToArray();
            int failures = tested.Count(n => n.Outcome == ProbeOutcome.Timeout);
            matches.Add(tested.Length == 0 ? null : condition.Metric switch
            {
                // All-timeout requires every member to have completed; partial coverage cannot assert 'all'.
                "all-timeout" => tested.Length == group.Length && failures == tested.Length,
                "timeout-percent" when condition.Threshold is >= 0 and <= 100 => 100.0 * failures / tested.Length >= condition.Threshold,
                "success-below" when condition.Threshold is >= 0 and <= 100 => 100.0 * (tested.Length - failures) / tested.Length < condition.Threshold,
                _ => throw new ArgumentException("不支持的健康检测条件。")
            });
        }
        if (policy.MaximumRefreshAge is { } age)
            matches.Add(prior.LastSuccess is { } success && now - success >= age);
        bool failed = matches.Count > 0 && (policy.RequireAll ? matches.All(x => x == true) : matches.Any(x => x == true));
        int consecutive = failed ? prior.ConsecutiveFailures + 1 : 0;
        var state = prior with { ConsecutiveFailures = consecutive };
        if (!failed) return Result(false, known == 0 ? "结果未知；不触发刷新" : "未达到刷新条件", state);
        if (consecutive < policy.ConsecutiveRounds) return Result(false, "等待连续异常确认", state);
        if (prior.LastAttempt is { } attempt && now - attempt < policy.Cooldown) return Result(false, "刷新冷却中", state);
        return Result(true, "连续异常已确认", state with { LastAttempt = now, ConsecutiveFailures = 0 });
    }
}
[JsonSerializable(typeof(HealthState))]
[JsonSerializable(typeof(HealthPolicy))]
public sealed partial class HealthJson : JsonSerializerContext;
