using System.Text.Json.Nodes;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class RuntimeAssetPublicationTests
{
    private const string Json = """{"routing":{"rules":[{"domain":["ext:geosite.dat:cn","full:unchanged.invalid"],"ip":["ext:geoip.dat:cn"],"outboundTag":"nonhome"}]},"outbounds":[{"tag":"nonhome"}]}""";
    private static Dictionary<string, byte[]> Assets(string domain)
    {
        var dat = GeoRuleCompiler.Compile([new() { Tag = "cn", Domains = [new(DomainMatchKind.Suffix, domain)], IpCidrs = ["192.0.2.0/24"] }]);
        return new() { ["geosite.dat"] = dat.GeoSite, ["geoip.dat"] = dat.GeoIp };
    }
    [Fact]
    public async Task BootstrapCoreResolvesNewAndRollbackAssetsFromItsUnchangedDirectory()
    {
        var directory = new Dictionary<string, byte[]>();
        var bootstrap = RuntimeAssetPublication.Prepare("""{"routing":{"rules":[]}}""", directory);
        Assert.Empty(bootstrap.Files);
        var first = RuntimeAssetPublication.Prepare(Json, Assets("old.invalid"));
        var second = RuntimeAssetPublication.Prepare(Json, Assets("new.invalid"));
        foreach (var asset in first.Files.Concat(second.Files)) directory[asset.Key] = asset.Value;
        Assert.Equal(3, directory.Count); // Shared IP DAT reused; both distinct domain generations remain intact.
        string active = first.Json;
        await Assert.ThrowsAsync<RuntimeFailureException>(() => RuntimeRoutingUpdate.ReplaceAsync(first.Json, second.Json, (json, _) =>
        {
            Assert.Empty(RuntimeAssetValidator.Validate(json, directory)); // The core keeps its original asset root.
            active = json;
            if (json == second.Json) throw new IOException("Synthetic lost RPC reply after update");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));
        Assert.Equal(first.Json, active);
        var parsed = JsonNode.Parse(first.Json)!;
        Assert.Equal("full:unchanged.invalid", parsed["routing"]!["rules"]![0]!["domain"]![1]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Json)!["outbounds"], parsed["outbounds"]));
        Assert.All(directory.Keys, name => Assert.True(ConfigurationCoordinator.SafeArtifactName(name)));
    }
    [Theory]
    [InlineData("ext:../geosite.dat:cn")]
    [InlineData("ext:/tmp/geosite.dat:cn")]
    [InlineData("ext:geosite.dat:missing")]
    public void UnvalidatedReferencesCannotBePublished(string reference)
        => Assert.Throws<InvalidDataException>(() => RuntimeAssetPublication.Prepare(Json.Replace("ext:geosite.dat:cn", reference), Assets("example.invalid")));
    [Fact]
    public void RoutingFailureDiagnosisDoesNotRetainPathsOrServerCredentials()
    {
        var diagnostic = new RoutingUpdateDiagnosis();
        diagnostic.Observe("rpc error: open /private/path/secret.dat: no such file or directory password=secret");
        Assert.Equal("assets.missing", diagnostic.Code);
        diagnostic.Observe(new string('x', 5000) + "permission denied");
        Assert.Equal("assets.missing", diagnostic.Code);
    }
}
