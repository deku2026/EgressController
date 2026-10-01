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

        Assert.Equal(["example.com", "openai.com"], loaded.Domains.Select(route => route.Name));
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

        Assert.Equal("example.com", Assert.Single(profile.Load().Domains).Name);
        Assert.Equal("connections", ui.Load().ActivePage);
        Assert.Equal("chrome", ui.Load().AppsSearch);
        Assert.NotEqual(profile.ProfilePath, ui.StatePath);
    }

    [Theory]
    [InlineData("SchemaVersion", "UpstreamPort", "EsimDomains")]
    [InlineData("schemaVersion", "upstreamPort", "esimDomains")]
    public void Legacy_json_migrates_without_losing_the_custom_default(string schema, string port, string domains)
    {
        var store = new EgressProfileStore(_directory);
        Directory.CreateDirectory(_directory);
        string legacy = $$"""{"{{schema}}":1,"{{port}}":1080,"{{domains}}":["Example.com"]}""";
        File.WriteAllText(store.ProfilePath, legacy);

        EgressProfileDocument loaded = store.Load();
        Assert.Equal(legacy, File.ReadAllText(store.ProfilePath));
        Assert.Equal([1080], loaded.UpstreamPorts);
        Assert.Equal(EgressRouteTarget.DefaultAdapter, Assert.Single(loaded.Domains).Target);

        store.Save(loaded);
        Assert.DoesNotContain("EsimDomains", File.ReadAllText(store.ProfilePath));
        Assert.Equal(1080, store.Load().UpstreamPort);
        Assert.Equal("example.com", Assert.Single(store.Load().Domains).Name);
    }

    [Fact]
    public void Mixed_destinations_round_trip_through_aot_json_metadata()
    {
        var store = new EgressProfileStore(_directory);
        store.Save(new EgressProfileDocument
        {
            UpstreamPorts = [7890, 7891],
            UpstreamPort = 7891,
            DefaultAdapterId = "11111111-1111-1111-1111-111111111111",
            ProxyAdapterId = "22222222-2222-2222-2222-222222222222",
            Applications = [new() { DiscoveryKey = "browser", Target = EgressRouteTarget.ForPort(7890) }],
            RuleSets = [new() { Name = "google", Target = EgressRouteTarget.Default }],
            Domains = [new() { Name = "example.com", Target = EgressRouteTarget.DefaultAdapter }],
        });

        EgressProfileDocument loaded = store.Load();
        Assert.Equal(4, loaded.SchemaVersion);
        Assert.Equal("22222222-2222-2222-2222-222222222222", loaded.ProxyAdapterId);
        Assert.Equal(2, loaded.Adapters.Count);
        Assert.Equal(7891, loaded.UpstreamPort);
        Assert.Equal([7890, 7891], loaded.UpstreamPorts);
        Assert.Equal(EgressRouteTarget.ForPort(7890), Assert.Single(loaded.Applications).Target);
        Assert.Equal(EgressRouteTarget.Default, Assert.Single(loaded.RuleSets).Target);
        Assert.Equal(EgressRouteTarget.DefaultAdapter, Assert.Single(loaded.Domains).Target);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
