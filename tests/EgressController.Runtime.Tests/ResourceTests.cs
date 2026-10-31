using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ResourceTests
{
    private static ResourceDescriptor Resource(string id = "catalog", string adapter = "saved-service") => new()
    { Id = id, Url = new("https://resources.example.invalid/" + id), Exit = ResourceExitSnapshot.Bound(new(adapter, adapter == "saved-service" ? "en5" : "en7")) };

    [Fact]
    public async Task ValidLocalCacheAvoidsRemoteAndCorruptionRecoversThroughSelectedInterface()
    {
        var cache = new InMemoryResourceCache(); var transport = new FixtureResourceTransport();
        var resource = Resource(); transport.Add(resource.Url, [1, 2, 3]);
        await cache.WriteAsync(ResourcePreparation.CacheKey(resource), [1, 9], CancellationToken.None);
        var preparation = new ResourcePreparation(transport, cache);
        Assert.Equal(new byte[] { 1, 9 }, await preparation.PrepareAsync(resource, bytes => bytes[0] == 1, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.RequestCount);
        await cache.WriteAsync(ResourcePreparation.CacheKey(resource), [0], CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, await preparation.PrepareAsync(resource, bytes => bytes[0] == 1, TestContext.Current.CancellationToken));
        Assert.Equal("saved-service", transport.UsedInterfaces.Single().StableId);
    }

    [Fact]
    public async Task ExplicitRefreshAndChangedUrlCannotRemainPinnedToOldCachedBytes()
    {
        var cache = new InMemoryResourceCache(); var transport = new FixtureResourceTransport();
        var resource = Resource(); transport.Add(resource.Url, [1]);
        var preparation = new ResourcePreparation(transport, cache);
        Assert.Equal(new byte[] { 1 }, await preparation.PrepareAsync(resource, _ => true, TestContext.Current.CancellationToken));
        transport.Add(resource.Url, [2]);
        Assert.Equal(new byte[] { 1 }, await preparation.PrepareAsync(resource, _ => true, TestContext.Current.CancellationToken));
        var refresh = new ResourceDescriptor { Id = resource.Id, Url = resource.Url, Exit = resource.Exit, CachePolicy = PreparationCachePolicy.Refresh };
        Assert.Equal(new byte[] { 2 }, await preparation.PrepareAsync(refresh, _ => true, TestContext.Current.CancellationToken));
        var changedUrl = new ResourceDescriptor { Id = resource.Id, Url = new("https://resources.example.invalid/updated-source"), Exit = resource.Exit };
        transport.Add(changedUrl.Url, [3]);
        Assert.Equal(new byte[] { 3 }, await preparation.PrepareAsync(changedUrl, _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(3, transport.RequestCount);
    }

    [Fact]
    public async Task ParallelCatalogAndPendingProfileKeepTheirOwnInterfaceSnapshots()
    {
        var transport = new FixtureResourceTransport();
        var saved = Resource("catalog"); var pending = Resource("core", "pending-service");
        transport.Add(saved.Url, [1]); transport.Add(pending.Url, [2]);
        await Task.WhenAll(new ResourcePreparation(transport, new InMemoryResourceCache()).PrepareAsync(saved, _ => true, TestContext.Current.CancellationToken),
            new ResourcePreparation(transport, new InMemoryResourceCache()).PrepareAsync(pending, _ => true, TestContext.Current.CancellationToken));
        Assert.Contains(transport.UsedInterfaces, item => item.StableId == "saved-service");
        Assert.Contains(transport.UsedInterfaces, item => item.StableId == "pending-service");
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationRemainDistinct()
    {
        var timeout = new ResourceDescriptor { Id = "rules", Url = new("https://resources.example.invalid/rules"), Exit = ResourceExitSnapshot.Bound(new("service", "en5")), Timeout = TimeSpan.FromMilliseconds(20) };
        var preparation = new ResourcePreparation(new WaitingTransport(), new InMemoryResourceCache());
        var error = await Assert.ThrowsAsync<ResourcePreparationException>(() => preparation.PrepareAsync(timeout, _ => true, TestContext.Current.CancellationToken));
        Assert.True(error.IsTimeout); Assert.Equal(443, error.RemotePort); Assert.Equal(PreparationStage.Downloading, error.Stage);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var fresh = new ResourcePreparation(new WaitingTransport(), new InMemoryResourceCache());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fresh.PrepareAsync(Resource(), _ => true, caller.Token));
        Assert.Empty(fresh.History);
    }

    [Fact]
    public async Task ObsoletePreparationCannotOverwriteItsReplacementState()
    {
        var transport = new ReplacingTransport();
        var preparation = new ResourcePreparation(transport, new InMemoryResourceCache());
        Task<byte[]> old = preparation.PrepareAsync(Resource("old"), _ => true, TestContext.Current.CancellationToken);
        await transport.Started.Task;
        Assert.Equal(new byte[] { 2 }, await preparation.PrepareAsync(Resource("new"), _ => true, TestContext.Current.CancellationToken));
        transport.Release.SetResult([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal("new", preparation.Progress.ResourceId);
        Assert.Equal(PreparationStage.Ready, preparation.Progress.Stage);
    }
    private sealed class WaitingTransport : IResourceTransport
    {
        public async Task<byte[]> DownloadAsync(ResourceDescriptor resource, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return []; }
    }
    private sealed class ReplacingTransport : IResourceTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> DownloadAsync(ResourceDescriptor resource, CancellationToken cancellationToken)
        { if (resource.Id == "new") return [2]; Started.SetResult(); return await Release.Task; }
    }
}
