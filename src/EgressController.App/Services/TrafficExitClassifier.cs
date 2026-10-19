using EgressController.SingBox.Configuration;
using EgressController.State.Traffic;

namespace EgressController.App.Services;

public static class TrafficExitClassifier
{
    public static TrafficExit Classify(string? outbound, string? homeAdapterId)
    {
        if (outbound is null) return TrafficExit.Unknown;
        if (outbound.StartsWith("adapter-", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(outbound[8..], "N", out var adapter) && Guid.TryParse(homeAdapterId, out var home))
            return adapter == home ? TrafficExit.Home : TrafficExit.Other;
        if (outbound.StartsWith("upstream-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(outbound[9..], out int port) && port is > 0 and <= 65535
            || string.Equals(outbound, EgressProfileCompiler.ProxyDirectTag, StringComparison.OrdinalIgnoreCase)) return TrafficExit.Other;
        return TrafficExit.Unknown;
    }
}
