namespace EgressController.State;

internal static class SqliteRuntime
{
    private static readonly Lazy<bool> Initialized = new(() => { SQLitePCL.Batteries_V2.Init(); return true; });
    public static void EnsureInitialized() => _ = Initialized.Value;
}
