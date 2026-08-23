using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Rules.Artifacts;
using EgressController.Rules.Catalog;

namespace EgressController.Rules.Tests;

public sealed class SingRuleCatalogTests
{
    [Fact]
    public async Task Update_indexes_only_srs_and_search_stays_local()
    {
        int fetches = 0;
        var fetcher = new FakeFetcher(uri =>
        {
            Interlocked.Increment(ref fetches);
            string path = uri.AbsolutePath;
            if (path.EndsWith("/git/ref/heads/sing", StringComparison.Ordinal))
                return Json($"{{\"object\":{{\"sha\":\"{Sha('a')}\"}}}}");
            if (path.EndsWith("/git/commits/" + Sha('a'), StringComparison.Ordinal))
                return Json($"{{\"tree\":{{\"sha\":\"{Sha('b')}\"}}}}");
            if (path.EndsWith("/git/trees/" + Sha('b'), StringComparison.Ordinal))
                return Json($"{{\"truncated\":false,\"tree\":[{{\"path\":\"geo\",\"type\":\"tree\",\"sha\":\"{Sha('c')}\"}}]}}");
            if (path.EndsWith("/git/trees/" + Sha('c'), StringComparison.Ordinal))
                return Json($"{{\"truncated\":false,\"tree\":[{{\"path\":\"geosite\",\"type\":\"tree\",\"sha\":\"{Sha('d')}\"}}]}}");
            if (path.EndsWith("/git/trees/" + Sha('d'), StringComparison.Ordinal))
                return Json($"{{\"truncated\":false,\"tree\":["
                    + $"{{\"path\":\"115.srs\",\"type\":\"blob\",\"sha\":\"{Sha('e')}\",\"size\":154}},"
                    + $"{{\"path\":\"google.srs\",\"type\":\"blob\",\"sha\":\"{Sha('f')}\",\"size\":7912}},"
                    + $"{{\"path\":\"openai.srs\",\"type\":\"blob\",\"sha\":\"{Sha('0')}\",\"size\":435}},"
                    + "{\"path\":\"legacy.list\",\"type\":\"blob\",\"sha\":\"1111111111111111111111111111111111111111\"},"
                    + "{\"path\":\"nested.srs\",\"type\":\"tree\",\"sha\":\"1111111111111111111111111111111111111111\"}]}");
            return new RemoteFetchResult(false, 404, null);
        });
        string root = NewRoot();
        try
        {
            var service = new RuleCatalogService(fetcher, root);
            SingBoxCatalogUpdateResult result = await service.UpdateAsync(TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.Error);
            Assert.NotNull(result.Catalog);
            Assert.Equal(3, result.Catalog!.Count);
            Assert.Equal(new[] { "115", "google", "openai" },
                result.Catalog.Entries.Select(entry => entry.Name));
            Assert.Equal("geo/geosite/google.srs", result.Catalog.Search("GOOG").Single().Path);
            int fetchesAfterUpdate = fetches;

            Assert.Single(service.Search("g", 20));
            Assert.Equal(fetchesAfterUpdate, fetches);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Failed_refresh_keeps_the_previous_cached_catalog_available_offline()
    {
        string root = NewRoot();
        try
        {
            var first = new RuleCatalogService(new FakeFetcher(BuildCatalogTree), root);
            SingBoxCatalogUpdateResult initial = await first.UpdateAsync(TestContext.Current.CancellationToken);
            Assert.True(initial.Succeeded, initial.Error);

            var offline = new RuleCatalogService(
                new FakeFetcher(_ => new RemoteFetchResult(false, 503, null)),
                root);
            SingBoxRuleCatalog? cached = offline.LoadCached(out string? loadError);
            Assert.NotNull(cached);
            Assert.Null(loadError);

            SingBoxCatalogUpdateResult failed = await offline.UpdateAsync(TestContext.Current.CancellationToken);

            Assert.False(failed.Succeeded);
            Assert.Equal(initial.Catalog!.Snapshot.CommitSha, offline.Current!.Snapshot.CommitSha);
            Assert.Single(offline.Search("google"));
            Assert.True(File.Exists(Path.Combine(root, "catalog.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Truncated_tree_is_rejected_without_replacing_cached_catalog()
    {
        string root = NewRoot();
        try
        {
            var service = new RuleCatalogService(new FakeFetcher(BuildCatalogTree), root);
            SingBoxCatalogUpdateResult initial = await service.UpdateAsync(TestContext.Current.CancellationToken);
            Assert.True(initial.Succeeded, initial.Error);

            var truncated = new RuleCatalogService(new FakeFetcher(uri =>
                uri.AbsolutePath.EndsWith("/git/ref/heads/sing", StringComparison.Ordinal)
                    ? Json($"{{\"object\":{{\"sha\":\"{Sha('a')}\"}}}}")
                    : uri.AbsolutePath.EndsWith("/git/commits/" + Sha('a'), StringComparison.Ordinal)
                        ? Json($"{{\"tree\":{{\"sha\":\"{Sha('b')}\"}}}}")
                        : uri.AbsolutePath.EndsWith("/git/trees/" + Sha('b'), StringComparison.Ordinal)
                            ? Json("{\"truncated\":true,\"tree\":[]}")
                            : new RemoteFetchResult(false, 404, null)), root);

            SingBoxCatalogUpdateResult failed = await truncated.UpdateAsync(TestContext.Current.CancellationToken);

            Assert.False(failed.Succeeded);
            Assert.Contains("截断", failed.Error);
            Assert.NotNull(truncated.LoadCached(out _));
            Assert.Equal(initial.Catalog!.Snapshot.TreeSha, truncated.Current!.Snapshot.TreeSha);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static RemoteFetchResult BuildCatalogTree(Uri uri)
    {
        string path = uri.AbsolutePath;
        if (path.EndsWith("/git/ref/heads/sing", StringComparison.Ordinal))
            return Json($"{{\"object\":{{\"sha\":\"{Sha('a')}\"}}}}");
        if (path.EndsWith("/git/commits/" + Sha('a'), StringComparison.Ordinal))
            return Json($"{{\"tree\":{{\"sha\":\"{Sha('b')}\"}}}}");
        if (path.EndsWith("/git/trees/" + Sha('b'), StringComparison.Ordinal))
            return Json($"{{\"truncated\":false,\"tree\":[{{\"path\":\"geo\",\"type\":\"tree\",\"sha\":\"{Sha('c')}\"}}]}}");
        if (path.EndsWith("/git/trees/" + Sha('c'), StringComparison.Ordinal))
            return Json($"{{\"truncated\":false,\"tree\":[{{\"path\":\"geosite\",\"type\":\"tree\",\"sha\":\"{Sha('d')}\"}}]}}");
        if (path.EndsWith("/git/trees/" + Sha('d'), StringComparison.Ordinal))
            return Json($"{{\"truncated\":false,\"tree\":[{{\"path\":\"google.srs\",\"type\":\"blob\",\"sha\":\"{Sha('e')}\",\"size\":10}}]}}");
        return new RemoteFetchResult(false, 404, null);
    }

    private static string Sha(char value) => new(value, 40);

    private static RemoteFetchResult Json(string body)
        => new(true, 200, Encoding.UTF8.GetBytes(body));

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "EgressController.SingRulesTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class FakeFetcher(Func<Uri, RemoteFetchResult> handler) : IRemoteFetcher
    {
        public ValueTask<RemoteFetchResult> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(handler(uri));
    }
}

public sealed class RuleArtifactStoreTests
{
    [Fact]
    public async Task Concurrent_requests_for_one_srs_share_one_download_and_publish_atomically()
    {
        byte[] body = Encoding.UTF8.GetBytes("synthetic-srs-payload");
        string commit = new('a', 40);
        var snapshot = MakeSnapshot(commit, ("google", body));
        int fetches = 0;
        var fetcher = new CountingFetcher(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref fetches);
            await Task.Delay(50, cancellationToken);
            return new RemoteFetchResult(true, 200, body);
        });
        string root = Path.Combine(Path.GetTempPath(), "EgressController.SrsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuleArtifactStore(root, fetcher);
            Task<RuleArtifactResult>[] requests = Enumerable.Range(0, 12)
                .Select(_ => store.EnsureAsync(snapshot, "google", TestContext.Current.CancellationToken))
                .ToArray();
            RuleArtifactResult[] results = await Task.WhenAll(requests);

            Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
            Assert.Equal(1, fetches);
            string path = results[0].Path!;
            Assert.True(File.Exists(path));
            Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.EndsWith("google.srs", path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Digest_failure_leaves_no_final_or_temporary_artifact()
    {
        byte[] body = Encoding.UTF8.GetBytes("not-the-expected-srs");
        string root = Path.Combine(Path.GetTempPath(), "EgressController.SrsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new SingBoxRuleCatalogSnapshot(
                new('b', 40),
                new('c', 40),
                new[]
                {
                    new SingBoxRuleCatalogEntry("openai", "geo/geosite/openai.srs", new('d', 40), body.Length),
                });
            var store = new RuleArtifactStore(root, new CountingFetcher((_, _) =>
                Task.FromResult(new RemoteFetchResult(true, 200, body))));

            RuleArtifactResult result = await store.EnsureAsync(snapshot, "openai", TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.False(store.TryGetPath(snapshot, "openai", out _));
            Assert.False(Directory.Exists(Path.Combine(root, "rules", snapshot.CommitSha)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Corrupt_existing_cache_is_not_reused()
    {
        byte[] body = Encoding.UTF8.GetBytes("valid-srs");
        string commit = new('c', 40);
        SingBoxRuleCatalogSnapshot snapshot = MakeSnapshot(commit, ("google", body));
        string root = Path.Combine(Path.GetTempPath(), "EgressController.SrsTests", Guid.NewGuid().ToString("N"));
        string cachedPath = Path.Combine(root, "rules", commit, "google.srs");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        await File.WriteAllTextAsync(cachedPath, "corrupt", TestContext.Current.CancellationToken);
        int downloads = 0;
        try
        {
            var store = new RuleArtifactStore(root, new CountingFetcher((_, _) =>
            {
                Interlocked.Increment(ref downloads);
                return Task.FromResult(new RemoteFetchResult(true, 200, body));
            }));

            RuleArtifactResult result = await store.EnsureAsync(snapshot, "google", TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(1, downloads);
            Assert.Equal(body, await File.ReadAllBytesAsync(cachedPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Batch_download_reports_all_failures_and_deduplicates_names()
    {
        byte[] google = Encoding.UTF8.GetBytes("google-srs");
        byte[] openai = Encoding.UTF8.GetBytes("openai-srs");
        var snapshot = MakeSnapshot(new('e', 40), ("google", google), ("openai", openai));
        var fetcher = new CountingFetcher((uri, _) =>
        {
            if (uri.AbsolutePath.EndsWith("/openai.srs", StringComparison.Ordinal))
                return Task.FromResult(new RemoteFetchResult(false, 503, null));
            return Task.FromResult(new RemoteFetchResult(true, 200, google));
        });
        string root = Path.Combine(Path.GetTempPath(), "EgressController.SrsTests", Guid.NewGuid().ToString("N"));
        try
        {
            RuleArtifactBatchResult result = await new RuleArtifactStore(root, fetcher).EnsureManyAsync(
                snapshot,
                new[] { "google", "GOOGLE", "openai" },
                maxParallelism: 2,
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Single(result.Paths);
            Assert.True(result.Paths.ContainsKey("google"));
            Assert.Single(result.Failures);
            Assert.Contains("openai", result.Failures.Keys);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static SingBoxRuleCatalogSnapshot MakeSnapshot(string commit, params (string Name, byte[] Body)[] entries)
        => new(
            commit,
            new('f', 40),
            entries.Select(item => new SingBoxRuleCatalogEntry(
                item.Name,
                "geo/geosite/" + item.Name + ".srs",
                GitBlobSha1(item.Body),
                item.Body.Length)).ToArray());

    private static string GitBlobSha1(byte[] body)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
        using var sha1 = SHA1.Create();
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        sha1.TransformFinalBlock(body, 0, body.Length);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class CountingFetcher(
        Func<Uri, CancellationToken, Task<RemoteFetchResult>> handler) : IRemoteFetcher
    {
        public async ValueTask<RemoteFetchResult> FetchAsync(
            Uri uri,
            int maxBytes,
            CancellationToken cancellationToken = default)
            => await handler(uri, cancellationToken);
    }
}
