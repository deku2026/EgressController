namespace EgressController.Core.Protection;

public static class RuntimeSafety
{
    public static void RequireLiveOperations()
    {
        if (Environment.GetEnvironmentVariable("EGRESS_MOCK_ONLY") == "1")
            throw new InvalidOperationException("Mock-only tests cannot start real applications/TUN or terminate real processes.");
    }
}
