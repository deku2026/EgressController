using EgressController.Core.Models;
using EgressController.State.Json;
using EgressController.State.Storage;

namespace EgressController.State.Tests;

public class AtomicJsonFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ec-store-" + Guid.NewGuid().ToString("N"));
    private readonly ProxyStateStore _store;

    public AtomicJsonFileTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ProxyStateStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ProxyPath => Path.Combine(_dir, "proxy-state.json");

    [Fact]
    public void Save_then_load_round_trips()
    {
        var rec = new ProxyStateRecord(Guid.NewGuid(), SystemProxyState.Off, SystemProxyState.Ours(), true, DateTimeOffset.UtcNow);
        _store.Save(rec);

        var loaded = _store.Load();
        Assert.Equal(rec.SessionId, loaded.SessionId);
        Assert.True(loaded.Active);
        Assert.Equal(rec.Ours!.Server, loaded.Ours!.Server);
        Assert.Equal(rec.Ours.ProxyOverride, loaded.Ours.ProxyOverride);
    }

    [Fact]
    public void Atomic_write_leaves_no_tmp_file()
    {
        _store.Save(new ProxyStateRecord(Guid.NewGuid(), null, null, true, DateTimeOffset.UtcNow));
        Assert.False(File.Exists(ProxyPath + ".tmp"));
        Assert.True(File.Exists(ProxyPath));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_default_returned()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ProxyPath, "<!DOCTYPE html><html>not json</html>");
        string? quarantined = null;

        var loaded = AtomicJsonFile.Read(ProxyPath, EgressStateJsonContext.Default.ProxyStateRecord,
            new ProxyStateRecord(Guid.Empty, null, null, false, DateTimeOffset.MinValue),
            d => quarantined = d);

        Assert.False(loaded.Active);
        Assert.NotNull(quarantined);                                   // gone to quarantine
        Assert.Matches(".corrupt\\.", quarantined!);
        Assert.False(File.Exists(ProxyPath));                          // original removed from active path
    }

    [Fact]
    public void Missing_file_returns_default_without_throwing()
    {
        var loaded = _store.Load();
        Assert.False(loaded.Active);
    }
}