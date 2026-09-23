using EgressController.Core.Profile;

namespace EgressController.Core.Tests;

public sealed class EgressProfileDocumentTests
{
    [Fact]
    public void Normalization_deduplicates_and_sorts_all_user_collections()
    {
        var profile = new EgressProfileDocument
        {
            EsimApplications =
            [
                new() { DiscoveryKey = "z-app" },
                new() { DiscoveryKey = "a-app" },
                new() { DiscoveryKey = "z-app" },
            ],
            EsimRuleSets = ["Google", "openai", "google"],
            EsimDomains = [" Example.COM. ", "例子.中国", "example.com"],
        }.NormalizeAndValidate();

        Assert.Equal(["a-app", "z-app"], profile.Applications.Select(x => x.DiscoveryKey));
        Assert.Equal(["google", "openai"], profile.RuleSets.Select(route => route.Name));
        Assert.Equal(["example.com", "xn--fsqu00a.xn--fiqs8s"], profile.Domains.Select(route => route.Name));
    }

    [Fact]
    public void Invalid_profile_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EgressProfileDocument { UpstreamPort = 0 }.NormalizeAndValidate());
        Assert.Throws<ArgumentException>(() => new EgressProfileDocument
        {
            PrimaryAdapterId = Guid.Empty.ToString(),
            EsimAdapterId = Guid.Empty.ToString(),
        }.NormalizeAndValidate());
        Assert.Throws<ArgumentException>(() => new EgressProfileDocument
        {
            EsimDomains = ["https://example.com"],
        }.NormalizeAndValidate());
        Assert.Throws<ArgumentException>(() => new EgressProfileDocument
        {
            Core = new EgressCoreSelection { Mode = "system" },
        }.NormalizeAndValidate());
    }

    [Fact]
    public void Unknown_schema_is_not_silently_downgraded()
    {
        var exception = Assert.Throws<ProfileSchemaException>(() => new EgressProfileDocument { SchemaVersion = 99 }.NormalizeAndValidate());
        Assert.Equal(99, exception.SchemaVersion);
    }

    [Fact]
    public void Version_one_preserves_custom_port_and_esim_selections()
    {
        var profile = new EgressProfileDocument
        {
            SchemaVersion = 1,
            UpstreamPort = 1080,
            EsimApplications = [new() { DiscoveryKey = "browser" }],
            EsimRuleSets = ["Google"],
            EsimDomains = ["Example.com"],
        }.NormalizeAndValidate();

        Assert.Equal(2, profile.SchemaVersion);
        Assert.Equal([1080], profile.UpstreamPorts);
        Assert.Equal(1080, profile.UpstreamPort);
        Assert.Equal(EgressRouteTarget.Esim, Assert.Single(profile.Applications).Target);
        Assert.Equal(EgressRouteTarget.Esim, Assert.Single(profile.RuleSets).Target);
        Assert.Equal("example.com", Assert.Single(profile.Domains).Name);
        Assert.Null(profile.EsimApplications);
        Assert.Null(profile.EsimRuleSets);
        Assert.Null(profile.EsimDomains);
        Assert.Equal(profile.Domains, profile.NormalizeAndValidate().Domains);
    }

    [Fact]
    public void Ports_are_unique_and_default_must_belong_to_the_list()
    {
        Assert.Equal(7890, EgressProfileDocument.Default.UpstreamPort);
        var profile = new EgressProfileDocument { UpstreamPorts = [7892, 7890, 7892] }.NormalizeAndValidate();
        Assert.Equal([7890, 7892], profile.UpstreamPorts);
        Assert.Throws<ArgumentException>(() => profile.SetDefaultPort(7891));
        Assert.Throws<ArgumentException>(() => (profile with { UpstreamPorts = [] }).NormalizeAndValidate());
        Assert.Throws<ArgumentException>(() => profile.AddPort(65536));
        Assert.Throws<ArgumentException>(() => profile.AddPort(0));
    }

    [Fact]
    public void Changing_default_keeps_explicit_assignments_and_protects_referenced_ports()
    {
        var profile = new EgressProfileDocument
        {
            UpstreamPorts = [7890, 7891, 7892, 7893, 7894],
            Applications = [new() { DiscoveryKey = "browser", Target = EgressRouteTarget.ForPort(7891) }],
            Domains = [new() { Name = "example.com", Target = EgressRouteTarget.ForPort(7892) }],
            RuleSets = [new() { Name = "google", Target = EgressRouteTarget.ForPort(7893) }],
        }.NormalizeAndValidate();

        foreach (int port in new[] { 7890, 7891, 7892, 7893 })
            Assert.Throws<ArgumentException>(() => profile.RemovePort(port));
        Assert.DoesNotContain(7894, profile.RemovePort(7894).UpstreamPorts);

        EgressProfileDocument changed = profile.SetDefaultPort(7894);
        Assert.Equal(7894, changed.UpstreamPort);
        Assert.Equal(7891, Assert.Single(changed.Applications).Target.Port);
        Assert.Equal(7892, Assert.Single(changed.Domains).Target.Port);
        Assert.DoesNotContain(7890, changed.RemovePort(7890).UpstreamPorts);
    }

    [Fact]
    public void Invalid_or_conflicting_destinations_are_not_silently_rerouted()
    {
        foreach (EgressRouteTarget target in new[]
        {
            EgressRouteTarget.ForPort(7891),
            new EgressRouteTarget { Kind = "port" },
            new EgressRouteTarget { Kind = "esim", Port = 7890 },
            new EgressRouteTarget { Kind = "unknown" },
        })
        {
            Assert.Throws<ArgumentException>(() => new EgressProfileDocument
            {
                Domains = [new() { Name = "example.com", Target = target }],
            }.NormalizeAndValidate());
        }
        Assert.Throws<ArgumentException>(() => new EgressProfileDocument
        {
            Domains =
            [
                new() { Name = "EXAMPLE.COM" },
                new() { Name = "example.com", Target = EgressRouteTarget.Default },
            ],
        }.NormalizeAndValidate());
    }
}
