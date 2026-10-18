namespace EgressController.Core.Protection;

public static class RuntimeSafety
{
    private static int _mockOnly;
    public static void EnableMockOnly() => Interlocked.Exchange(ref _mockOnly, 1);
    public static void RequireLiveOperations()
    {
        if (Volatile.Read(ref _mockOnly) != 0 || Environment.GetEnvironmentVariable("EGRESS_MOCK_ONLY") == "1")
            throw new InvalidOperationException("Mock-only tests cannot start real applications/TUN or terminate real processes.");
    }
}
