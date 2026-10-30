namespace EgressController.Runtime;

public enum GuardianState { Healthy, Protecting, Unavailable, ManuallyStopped }
public sealed record GuardianObservation(bool AuthenticatedMainAlive, bool TunTakenOver, bool ManuallyStopped,
    bool ReadSucceeded, DateTimeOffset ObservedAt);
public sealed record GuardianVerdict(GuardianState State, string Reason);

/// <summary>The guardian never accepts an Internet, DNS, latency or telemetry observation.</summary>
public static class GuardianEvaluator
{
    public static GuardianVerdict Evaluate(GuardianObservation observation, DateTimeOffset now, TimeSpan? maxAge = null)
    {
        if (!observation.ReadSucceeded || now - observation.ObservedAt > (maxAge ?? TimeSpan.FromSeconds(15))
            || observation.ObservedAt > now.AddSeconds(5))
            return new(GuardianState.Unavailable, "无法取得当前守护状态。");
        if (observation.ManuallyStopped) return new(GuardianState.ManuallyStopped, "守护已手动停止。");
        if (!observation.AuthenticatedMainAlive || !observation.TunTakenOver)
            return new(GuardianState.Protecting, "正在保护所选应用，等待主程序与 TUN 就绪。");
        return new(GuardianState.Healthy, "主程序身份与 TUN 接管均已确认。");
    }
}

/// <summary>Main-process policy: 30-second rounds, 5-second retries, three consecutive failures.</summary>
public sealed class NetworkReadinessMonitor
{
    private DateTimeOffset? _lastObservation;
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset NextProbeAt { get; private set; } = DateTimeOffset.MinValue;
    public bool LinkAvailable { get; private set; }
    public bool IpAvailable { get; private set; }
    public bool InternetConfirmed { get; private set; }
    public bool Ready => LinkAvailable && IpAvailable && InternetConfirmed;

    public void ObserveLink(bool linkAvailable, bool ipAvailable)
    { LinkAvailable = linkAvailable; IpAvailable = ipAvailable; }

    public bool ObserveInternet(bool success, DateTimeOffset observedAt)
    {
        if (_lastObservation is { } last && (observedAt <= last || observedAt < NextProbeAt)) return false;
        _lastObservation = observedAt;
        if (success)
        {
            ConsecutiveFailures = 0;
            InternetConfirmed = true;
            NextProbeAt = observedAt.AddSeconds(30);
        }
        else
        {
            ConsecutiveFailures = Math.Min(3, ConsecutiveFailures + 1);
            if (ConsecutiveFailures == 3) InternetConfirmed = false;
            NextProbeAt = observedAt.AddSeconds(ConsecutiveFailures < 3 ? 5 : 30);
        }
        return true;
    }
}
