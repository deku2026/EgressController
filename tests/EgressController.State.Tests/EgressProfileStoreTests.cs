using System.Text.Json;
using EgressController.Core.Profile;
using EgressController.State.Profile;
using EgressController.State.Ui;

namespace EgressController.State.Tests;

public sealed class EgressProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EgressController.ProfileTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Missing_profile_loads_defaults_and_save_round_trips_normalized_data()
    {
        var store = new EgressProfileStore(_directory);
        Assert.Equal(7890, store.Load().UpstreamPort);

        store.Save(new EgressProfileDocument { EsimDomains = ["Example.com", "example.com"] });
        store.Save(new EgressProfileDocument { EsimDomains = ["Example.com", "example.com", "openai.com"] });
        EgressProfileDocument loaded = store.Load();

        Assert.Equal(["example.com", "openai.com"], loaded.EsimDomains);
        Assert.True(File.Exists(store.ProfilePath));
        Assert.True(File.Exists(store.ProfilePath + ".bak"));
    }

    [Fact]
    public void Unknown_schema_throws_without_overwriting_the_file()
    {
        var store = new EgressProfileStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.ProfilePath, "{\"schemaVersion\":99}");

        Assert.Throws<ProfileSchemaException>(() => store.Load());
        Assert.Contains("schemaVersion", File.ReadAllText(store.ProfilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_is_reported_as_a_store_error()
    {
        var store = new EgressProfileStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.ProfilePath, "not-json");

        Assert.Throws<ProfileStoreException>(() => store.Load());
    }

    [Fact]
    public void Ui_state_is_persisted_separately()
    {
        var profile = new EgressProfileStore(_directory);
        var ui = new UiStateStore(_directory);
        profile.Save(new EgressProfileDocument { EsimDomains = ["example.com"] });
        ui.Save(new UiStateDocument { ActivePage = "connections", AppsSearch = "chrome" });

        Assert.Equal("example.com", Assert.Single(profile.Load().EsimDomains));
        Assert.Equal("connections", ui.Load().ActivePage);
        Assert.Equal("chrome", ui.Load().AppsSearch);
        Assert.NotEqual(profile.ProfilePath, ui.StatePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
