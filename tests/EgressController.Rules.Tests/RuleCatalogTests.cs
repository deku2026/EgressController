using System.Text;
using EgressController.Core.Contracts;
using EgressController.Rules.Catalog;
using EgressController.Rules.Parsing;

namespace EgressController.Rules.Tests;

public class RuleCatalogTests
{
    private static RuleCatalog CatalogWith(params (string Name, string BlobSha)[] entries)
        => new(new RuleCatalogSnapshot("abc123", "tree99",
            entries.Select(e => new RuleCatalogEntry(e.Name, "geo/geosite/" + e.Name + ".list", e.BlobSha)).ToArray()));

    [Fact]
    public void Search_is_local_and_case_insensitive_substring()
    {
        int fetches = 0;
        var fetcher = new FakeFetcher((_) => { fetches++; return Fail(); }); // stub; search must not call it
        var catalog = CatalogWith(("google", "1"), ("google-cn", "2"), ("youtube", "3"));
        var manager = new RuleSnapshotManager(fetcher);
        manager.SetAvailableCatalog(catalog);

        var hits = catalog.Search("goo");
        Assert.Contains(hits, e => e.Name == "google");
        Assert.Contains(hits, e => e.Name == "google-cn");
        Assert.DoesNotContain(hits, e => e.Name == "youtube");
        Assert.Empty(catalog.Search("zzz"));
        Assert.Equal(0, fetches); // no network on search
    }

    [Fact]
    public async Task Successful_migration_activates_all_selected_from_same_commit()
    {
        var fetcher = new FakeFetcher(url =>
        {
            string name = url.AbsolutePath.Split('/')[^1];
            return Ok(name == "google" ? "google.com\n+.google.com" : "youtube.com\n+.youtube.com");
        });
        var manager = new RuleSnapshotManager(fetcher);
        manager.SetAvailableCatalog(CatalogWith(("google", "1"), ("youtube", "2")));

        var r = await manager.ActivateAsync(new[] { "google", "youtube" }, manager.Available!, TestContext.Current.CancellationToken);

        Assert.True(r.Succeeded);
        Assert.Equal("abc123", manager.Active.CommitSha);
        Assert.Equal(2, manager.Active.RuleSetNames.Count);
        Assert.Equal(4, manager.Active.Rules.Count);
    }

    [Fact]
    public async Task One_rule_download_failure_keeps_old_active_none_migrated()
    {
        var fetcher = new FakeFetcher(url =>
            url.AbsolutePath.Contains("/google.list") ? Ok("google.com") : Fail2(status: 500));
        var manager = new RuleSnapshotManager(fetcher);
        manager.SetAvailableCatalog(CatalogWith(("google", "1"), ("youtube", "2")));

        var before = manager.Active;
        var r = await manager.ActivateAsync(new[] { "google", "youtube" }, manager.Available!, TestContext.Current.CancellationToken);

        Assert.False(r.Succeeded);
        Assert.Equal("youtube", r.FailedName); // the first that failed
        // active unchanged — zero rules from the target got published
        Assert.Same(before, manager.Active);
        Assert.Empty(manager.Active.Rules);
    }

    [Fact]
    public async Task Html_response_is_rejected_and_active_unchanged()
    {
        var fetcher = new FakeFetcher(url => Ok("<!DOCTYPE html><html>truncated page</html>"));
        var manager = new RuleSnapshotManager(fetcher);
        manager.SetAvailableCatalog(CatalogWith(("google", "1")));

        var r = await manager.ActivateAsync(new[] { "google" }, manager.Available!, TestContext.Current.CancellationToken);
        Assert.False(r.Succeeded);
        Assert.Empty(manager.Active.Rules);
    }

    [Fact]
    public async Task Rule_with_unsupported_syntax_is_rejected_whole_migration()
    {
        var fetcher = new FakeFetcher(url =>
            url.AbsolutePath.Contains("/good.list") ? Ok("good.com\n+.good.com") : Ok("bad!!.com"));
        var manager = new RuleSnapshotManager(fetcher);
        manager.SetAvailableCatalog(CatalogWith(("good", "1"), ("bad", "2")));

        var r = await manager.ActivateAsync(new[] { "good", "bad" }, manager.Available!, TestContext.Current.CancellationToken);
        Assert.False(r.Succeeded);
        Assert.Equal("bad", r.FailedName);
        Assert.Empty(manager.Active.Rules); // good was downloaded but NOT published (all-or-nothing)
    }

    [Fact]
    public void Rule_download_url_is_commit_pinned()
    {
        var u = RuleSnapshotManager.RuleDownloadUri("deadbeef", "openai");
        Assert.Equal("https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/deadbeef/geo/geosite/openai.list", u.ToString());
    }

    [Fact]
    public async Task Meta_catalog_updater_walks_the_immutable_github_tree()
    {
        string commit = new('a', 40);
        string rootTree = new('b', 40);
        string geoTree = new('c', 40);
        string geositeTree = new('d', 40);
        string googleBlob = new('e', 40);
        string youtubeBlob = new('f', 40);

        var fetcher = new FakeFetcher(uri =>
        {
            string path = uri.AbsolutePath;
            if (path.EndsWith("/git/ref/heads/meta", StringComparison.Ordinal))
                return Ok($"{{\"object\":{{\"sha\":\"{commit}\",\"type\":\"commit\"}}}}");
            if (path.EndsWith("/git/commits/" + commit, StringComparison.Ordinal))
                return Ok($"{{\"tree\":{{\"sha\":\"{rootTree}\"}}}}");
            if (path.EndsWith("/git/trees/" + rootTree, StringComparison.Ordinal))
                return Ok($"{{\"sha\":\"{rootTree}\",\"truncated\":false,\"tree\":[{{\"path\":\"geo\",\"type\":\"tree\",\"sha\":\"{geoTree}\"}}]}}");
            if (path.EndsWith("/git/trees/" + geoTree, StringComparison.Ordinal))
                return Ok($"{{\"sha\":\"{geoTree}\",\"truncated\":false,\"tree\":[{{\"path\":\"geosite\",\"type\":\"tree\",\"sha\":\"{geositeTree}\"}}]}}");
            if (path.EndsWith("/git/trees/" + geositeTree, StringComparison.Ordinal))
                return Ok($"{{\"sha\":\"{geositeTree}\",\"truncated\":false,\"tree\":["
                    + $"{{\"path\":\"google.list\",\"type\":\"blob\",\"sha\":\"{googleBlob}\"}},"
                    + $"{{\"path\":\"youtube.list\",\"type\":\"blob\",\"sha\":\"{youtubeBlob}\"}},"
                    + "{\"path\":\"classical\",\"type\":\"tree\",\"sha\":\"1111111111111111111111111111111111111111\"}]}");
            return Fail2(404);
        });

        CatalogUpdateResult result = await new MetaRulesCatalogUpdater(fetcher)
            .FetchLatestAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Catalog);
        Assert.Equal(commit, result.Catalog!.Snapshot.CommitSha);
        Assert.Equal(geositeTree, result.Catalog.Snapshot.TreeSha);
        Assert.Equal("geo/geosite/google.list", result.Catalog.Search("google").Single().Path);
        Assert.Equal(googleBlob, result.Catalog.Search("google").Single().BlobSha);
        Assert.Empty(result.Catalog.Search("classical"));
    }

    [Fact]
    public async Task Meta_catalog_updater_rejects_truncated_tree()
    {
        string commit = new('a', 40);
        string rootTree = new('b', 40);
        var fetcher = new FakeFetcher(uri =>
        {
            string path = uri.AbsolutePath;
            if (path.EndsWith("/git/ref/heads/meta", StringComparison.Ordinal))
                return Ok($"{{\"object\":{{\"sha\":\"{commit}\"}}}}");
            if (path.EndsWith("/git/commits/" + commit, StringComparison.Ordinal))
                return Ok($"{{\"tree\":{{\"sha\":\"{rootTree}\"}}}}");
            if (path.EndsWith("/git/trees/" + rootTree, StringComparison.Ordinal))
                return Ok("{\"truncated\":true,\"tree\":[]}");
            return Fail2(404);
        });

        CatalogUpdateResult result = await new MetaRulesCatalogUpdater(fetcher)
            .FetchLatestAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("截断", result.Error);
    }

    [Fact]
    public void Rule_cache_round_trips_catalog_and_active_snapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController.RuleTests", Guid.NewGuid().ToString("N"));
        try
        {
            string commit = new('a', 40);
            var snapshot = new RuleCatalogSnapshot(
                commit,
                new string('b', 40),
                new[]
                {
                    new RuleCatalogEntry("google", "geo/geosite/google.list", new string('c', 40)),
                    new RuleCatalogEntry("youtube", "geo/geosite/youtube.list", new string('d', 40)),
                });
            var bodies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["google"] = Encoding.UTF8.GetBytes("google.com\n"),
                ["youtube"] = Encoding.UTF8.GetBytes("youtube.com\n"),
            };
            var store = new RuleCacheStore(root);

            store.SaveCatalog(snapshot);
            store.PublishActive(snapshot, bodies);

            Assert.True(store.TryLoadCatalog(out RuleCatalog? loadedCatalog, out string? catalogError), catalogError);
            Assert.Equal(commit, loadedCatalog!.Snapshot.CommitSha);
            Assert.True(store.TryLoadActive(out CachedActiveRules? active, out string? activeError), activeError);
            Assert.NotNull(active);
            Assert.Equal(new[] { "google", "youtube" }, active!.Manifest.SelectedNames);
            Assert.Equal(bodies["google"], active.Bodies["google"]);
            Assert.Equal(bodies["youtube"], active.Bodies["youtube"]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static RemoteFetchResult Ok(string body) => new(true, 200, Encoding.UTF8.GetBytes(body));
    private static RemoteFetchResult Fail() => new(false, null, null);
    private static RemoteFetchResult Fail2(int status) => new(false, status, null);

    private sealed class FakeFetcher(Func<Uri, RemoteFetchResult> handler) : IRemoteFetcher
    {
        public ValueTask<RemoteFetchResult> FetchAsync(Uri uri, int maxBytes, CancellationToken ct = default)
            => ValueTask.FromResult(handler(uri));
    }
}
