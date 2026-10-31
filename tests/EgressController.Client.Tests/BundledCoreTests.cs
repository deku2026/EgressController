using System.Security.Cryptography;
using EgressController.Runtime;

namespace EgressController.Client.Tests;

public sealed class BundledCoreTests
{
    [Fact]
    public async Task ExactPackagedArchiveRepairsPrivateCacheOfflineWithoutExecutingCore()
    {
        string archive = Path.Combine(AppContext.BaseDirectory, "core", "Xray-macos-arm64-v8a.zip");
        // The same Content item flows through IDE builds, tests and NativeAOT packaging.
        Assert.True(File.Exists(archive), "Acquire the pinned artifacts/core payload before running package boundary tests.");
        string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "egress-core-boundary-" + Guid.NewGuid().ToString("N"));
        var transport = new FixtureResourceTransport();
        var manager = new XrayCoreReleaseManager(transport, new InMemoryResourceCache(), root,
            _ => ValueTask.FromResult(ResourceExitSnapshot.Xray(17890)));
        try
        {
            var first = await manager.PrepareBundledAsync(archive, TestContext.Current.CancellationToken);
            Assert.Equal(XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256, first.Sha256);
            DateTime modified = File.GetLastWriteTimeUtc(first.ExecutablePath);
            var cached = await manager.PrepareBundledAsync(Path.Combine(root, "missing.zip"), TestContext.Current.CancellationToken);
            Assert.Equal(first, cached); Assert.Equal(modified, File.GetLastWriteTimeUtc(cached.ExecutablePath));
            await File.WriteAllBytesAsync(first.ExecutablePath, [1, 2, 3], TestContext.Current.CancellationToken);
            var recovered = await manager.PrepareBundledAsync(archive, TestContext.Current.CancellationToken);
            Assert.Equal(first, recovered);
            await using var stream = File.OpenRead(recovered.ExecutablePath);
            Assert.Equal(first.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken)));
            Assert.Equal(0, transport.RequestCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
