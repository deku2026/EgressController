using EgressController.State.SingBox;

namespace EgressController.State.Tests;

public sealed class SingBoxStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EgressController.SingBoxStateTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Runtime_and_pending_pointers_round_trip_and_pending_can_be_cleared()
    {
        var store = new SingBoxStateStore(_root);
        var pointer = new SingBoxRuntimePointer
        {
            Core = new SingBoxCorePointer
            {
                Version = "1.13.19",
                ExecutablePath = Path.Combine(_root, "core", "1.13.19", "sing-box.exe"),
                Sha256 = new('a', 64),
                VerifiedAtUtc = DateTimeOffset.UtcNow,
            },
            ConfigPath = Path.Combine(_root, "config.json"),
            ConfigSha256 = new('b', 64),
            AppliedAtUtc = DateTimeOffset.UtcNow,
        };
        store.SaveCurrentRuntime(pointer);
        store.SaveLastGoodRuntime(pointer);
        store.SavePendingApply(new SingBoxPendingApply
        {
            Candidate = pointer,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        Assert.Equal(pointer.ConfigSha256, store.LoadCurrentRuntime()!.ConfigSha256);
        Assert.Equal(pointer.Core.Version, store.LoadLastGoodRuntime()!.Core.Version);
        Assert.NotNull(store.LoadPendingApply());
        store.ClearPendingApply();
        Assert.Null(store.LoadPendingApply());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
