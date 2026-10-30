namespace EgressController.Desktop.Preview;
/// <summary>The executable enters isolation before constructing any application or service.</summary>
public static class PreviewEnvironment
{
    public static bool IsIsolated { get; private set; }
    public static string SessionDirectory { get; private set; } = "";
    public static void Enter()
    {
        if (IsIsolated) return;
        IsIsolated = true;
        Environment.SetEnvironmentVariable("EGRESS_MOCK_ONLY", "1");
        SessionDirectory = Path.Combine(Path.GetTempPath(), "EgressController", "mac-preview", Guid.NewGuid().ToString("N"));
    }
    public static void RequireIsolated()
    {
        if (!IsIsolated) throw new InvalidOperationException("Preview safety must be entered before initialization.");
    }
    public static void RequireLiveOperations() => throw new InvalidOperationException("Live network, process, service and startup operations are unavailable in this preview.");
}
