using EgressController.Core.Models;
using EgressController.State.Applications;
using Microsoft.Data.Sqlite;

namespace EgressController.State.Tests;

public sealed class ApplicationCatalogStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EgressController.CatalogTests", Guid.NewGuid().ToString("N"));
    private ApplicationCatalogStore Store() => new(_directory);
    private static LaunchTarget Target(bool ready = true) => new() { Id = "game", SavedDiscoveryKey = "steam:42", Name = "Example Game",
        CanonicalExecutable = @"D:\Games\Example\game.exe", OwnedRoots = [@"D:\Games\Example"],
        OwnedExecutables = ready ? [@"D:\Games\Example\game.exe", @"D:\Games\Example\tools\helper.exe"] : [],
        RelatedDiscoveryKeys = [@"exe:D:\Games\Example\game.exe"], Source = "Steam 游戏库", InventoryReady = ready };
    [Fact]
    public void Categories_are_empty_by_default_and_persist_without_enabling_routes()
    {
        var store = Store(); store.Save([Target()]); Assert.Empty(store.Categories());
        store.SetCategory(Target(), "games");
        var reopened = Store(); Assert.Equal("games", reopened.Categories()[Target().DiscoveryKey]);
        Assert.False(Assert.Single(reopened.Read()).RouteSelected);
        reopened.SetCategory(Target(), ""); Assert.Equal("", Store().Categories()[Target().DiscoveryKey]);
    }
    [Fact]
    public void Metadata_refresh_keeps_saved_executables_and_manual_categories()
    {
        var store = Store(); store.Save([Target()]); store.SetCategory(Target(), "ai"); store.Save([Target(false)]);
        var saved = Assert.Single(Store().Read());
        Assert.True(saved.InventoryReady); Assert.Equal(2, saved.OwnedExecutables.Count);
        Assert.Equal("ai", Store().Categories()[saved.DiscoveryKey]); // No classifier overwrites the user's decision.
    }
    [Fact]
    public void Categories_follow_provider_aliases_and_invalid_categories_do_not_overwrite()
    {
        var store = Store(); store.SetCategory(Target(), "games");
        Assert.Equal("games", store.Categories()[Target().RelatedDiscoveryKeys[0]]);
        Assert.Throws<ArgumentException>(() => store.SetCategory(Target(), "invented"));
        Assert.Equal("games", store.Categories()[Target().DiscoveryKey]);
    }
    [Fact]
    public void Inventory_write_is_atomic_when_storage_rejects_a_path()
    {
        var store = Store(); store.Save([Target(false)]);
        using var db = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_paths BEFORE INSERT ON app_paths WHEN NEW.kind=1 BEGIN SELECT RAISE(ABORT,'mock storage failure'); END";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.Save([Target()]));
        var cached = Assert.Single(Store().Read()); Assert.False(cached.InventoryReady); Assert.Empty(cached.OwnedExecutables);
    }
    [Fact]
    public void Newly_associated_root_stays_pending_after_restart_until_an_explicit_complete_inventory_is_saved()
    {
        var original = Target(); var store = Store();
        store.Save([original]); store.SetCategory(original, "ai");
        var pending = original.WithInventory(original.OwnedExecutables, false, [.. original.OwnedRoots, @"C:\Demo\Local\Packages\Example_pzs8sxrjxfjjc"]);
        store.Save([pending]);
        var reloaded = Assert.Single(Store().Read());
        Assert.False(reloaded.InventoryReady); Assert.Equal(2, reloaded.OwnedRoots.Count);
        Assert.Equal(original.OwnedExecutables, reloaded.OwnedExecutables); Assert.Equal("ai", Store().Categories()[original.DiscoveryKey]);
        store.Save([reloaded.WithInventory([.. reloaded.OwnedExecutables, @"C:\Demo\Local\Packages\Example_pzs8sxrjxfjjc\runtime.exe"])]);
        Assert.True(Assert.Single(Store().Read()).InventoryReady);
        Assert.Equal(3, Assert.Single(Store().Read()).OwnedExecutables.Count);
    }
    [Fact]
    public void Pending_snapshot_is_preserved_even_when_the_catalog_database_is_new()
    {
        var pending = Target().WithInventory(Target().OwnedExecutables, false);
        Store().Save([pending]);
        var restored = Assert.Single(Store().Read());
        Assert.False(restored.InventoryReady); Assert.Equal(pending.OwnedExecutables, restored.OwnedExecutables);
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_directory), parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EgressController.CatalogTests"));
        if (Path.GetDirectoryName(full) != parent) throw new InvalidOperationException("Unexpected test directory");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
