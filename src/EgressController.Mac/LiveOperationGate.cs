// SPDX-License-Identifier: MIT
namespace EgressController.Mac;

/// <summary>A denied gate is the default. Preview composition never requests a live gate.</summary>
public sealed class LiveOperationGate
{
    private readonly bool _explicitLive;
    public LiveOperationGate() { }
    private LiveOperationGate(bool explicitLive) => _explicitLive = explicitLive;

    public static LiveOperationGate ForExplicitLiveUse()
    {
        var gate = new LiveOperationGate(true);
        gate.Require("enable live macOS platform access");
        return gate;
    }

    public void Require(string operation)
    {
        if (!_explicitLive || Environment.GetEnvironmentVariable("EGRESS_MOCK_ONLY") == "1")
            throw new InvalidOperationException($"Mock safety denied macOS operation: {operation}.");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The macOS platform adapter requires macOS.");
    }
}
