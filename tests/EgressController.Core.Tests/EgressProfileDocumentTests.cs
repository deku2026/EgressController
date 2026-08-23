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

        Assert.Equal(["a-app", "z-app"], profile.EsimApplications.Select(x => x.DiscoveryKey));
        Assert.Equal(["google", "openai"], profile.EsimRuleSets);
        Assert.Equal(["example.com", "xn--fsqu00a.xn--fiqs8s"], profile.EsimDomains);
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
            Core = new EgressCoreSelection { Mode = EgressProfileSchema.SystemCore, SystemPath = "sing-box.exe" },
        }.NormalizeAndValidate());
    }

    [Fact]
    public void Unknown_schema_is_not_silently_downgraded()
    {
        var exception = Assert.Throws<ProfileSchemaException>(() => new EgressProfileDocument { SchemaVersion = 99 }.NormalizeAndValidate());
        Assert.Equal(99, exception.SchemaVersion);
    }
}
