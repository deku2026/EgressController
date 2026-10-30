// SPDX-License-Identifier: MIT
using EgressController.Mac;

namespace EgressController.Mac.Tests;

public sealed class IconAndSafetyTests
{
    [Fact]
    public void DefaultGateRejectsBeforeAnyNativeLibraryOrFilesystemAccess()
    {
        var gate = new LiveOperationGate();
        var native = new NativeMacPlatform(gate);
        Assert.Throws<InvalidOperationException>(() => native.ReadBundle("/Applications/Synthetic.app"));
        Assert.Throws<InvalidOperationException>(() => native.ReadPng("/Applications/Synthetic.app", 64));
        Assert.Throws<InvalidOperationException>(() => native.ReadInterfaces());
        Assert.Throws<InvalidOperationException>(() => native.ReadDnsSnapshots());
        Assert.Throws<InvalidOperationException>(() => new PhysicalMacCatalogFileSystem(gate, native).Enumerate("/Applications"));
        Assert.Throws<InvalidOperationException>(() => new PhysicalMacCatalogFileSystem(gate, native).IsExecutableMachO("/Applications/Synthetic.app/Contents/MacOS/Main"));
    }

    [Fact]
    public async Task SharedIconReadsRunAwayFromCallerAndReturnIsolatedBuffers()
    {
        var source = new RecordingIcons(); var cache = new MacIconCache(source);
        var pending = Enumerable.Range(0, 16).Select(_ => cache.GetPngAsync("/Applications/Synthetic.app", cancellationToken: TestContext.Current.CancellationToken)).ToArray();
        var values = await Task.WhenAll(pending);
        Assert.Equal(1, source.Reads);
        values[0][0] = 0;
        Assert.Equal(137, values[1][0]);
        Assert.Equal(137, (await cache.GetPngAsync("/Applications/Synthetic.app", cancellationToken: TestContext.Current.CancellationToken))[0]);
    }

    [Fact]
    public async Task CacheIsBoundedAndCancellationDoesNotDisposeAnotherReadersWork()
    {
        var source = new RecordingIcons(); var cache = new MacIconCache(source, capacity: 2);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetPngAsync("/Applications/A.app", cancellationToken: cancelled.Token));
        await cache.GetPngAsync("/Applications/A.app", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Reads);
        await cache.GetPngAsync("/Applications/B.app", cancellationToken: TestContext.Current.CancellationToken);
        await cache.GetPngAsync("/Applications/C.app", cancellationToken: TestContext.Current.CancellationToken);
        await cache.GetPngAsync("/Applications/A.app", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, source.Reads);
    }

    [Fact]
    public async Task NativeArtworkConcurrencyIsBounded()
    {
        var source = new RecordingIcons(); var cache = new MacIconCache(source);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => cache.GetPngAsync($"/Applications/App{i}.app", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.InRange(source.MaximumConcurrent, 1, 4);
        Assert.Equal(24, source.Reads);
    }

    [Fact]
    public async Task MissingPrivilegedHelperNeverPretendsToHaveTakenOverTun()
    {
        var request = new MacHelperRequest(1, Guid.NewGuid(), 1, MacHelperOperation.Inspect, null, null, null, null, null, null);
        var reply = await new UnavailableMacHelperTransport().SendAsync(request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(reply.Succeeded); Assert.False(reply.TunTakenOver); Assert.False(reply.DnsRestored);
        Assert.Equal("helper-not-installed", reply.Code);
    }

    private sealed class RecordingIcons : IMacIconSource
    {
        public int Reads, MaximumConcurrent;
        private int _active;
        public byte[] ReadPng(string canonicalBundlePath, int pixelSize)
        {
            Interlocked.Increment(ref Reads);
            int count = Interlocked.Increment(ref _active);
            int before;
            do { before = MaximumConcurrent; if (before >= count) break; }
            while (Interlocked.CompareExchange(ref MaximumConcurrent, count, before) != before);
            Thread.Sleep(15);
            Interlocked.Decrement(ref _active);
            return [137, 80, 78, 71];
        }
    }
}
