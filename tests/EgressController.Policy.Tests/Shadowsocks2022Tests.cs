using System.Text.Json;

namespace EgressController.Policy.Tests;

public sealed class Shadowsocks2022Tests
{
    [Theory]
    [InlineData("2022-blake3-aes-128-gcm", 16)]
    [InlineData("2022-blake3-aes-256-gcm", 32)]
    [InlineData("2022-blake3-chacha20-poly1305", 32)]
    public void CorrectFixedSizeKeysSurviveImportDslAndCompiledOutbound(string method, int keyLength)
    {
        var password = Key(keyLength); var node = Import(method, password);
        Assert.True(node.IsSupported);
        var policy = Policy(node); var roundtrip = PolicyYaml.Read(PolicyYaml.Write(policy)); Assert.True(roundtrip.IsSuccess);
        Assert.Equal(password, Assert.Single(roundtrip.Document!.Nodes).Password);
        var compiled = XrayConfigCompiler.Compile(roundtrip.Document, PolicyTests.Options()); Assert.True(compiled.IsSuccess);
        using var json = JsonDocument.Parse(compiled.Json!);
        var outbound = json.RootElement.GetProperty("outbounds").EnumerateArray().Single(x => x.GetProperty("tag").GetString() == node.Id);
        Assert.Equal(password, outbound.GetProperty("settings").GetProperty("password").GetString());
    }

    [Theory]
    [InlineData("2022-blake3-aes-128-gcm", 16)]
    [InlineData("2022-blake3-aes-256-gcm", 32)]
    public void AesColonChainsKeepEveryKeyInOrder(string method, int keyLength)
    {
        var password = Key(keyLength) + ":" + Key(keyLength, 64) + ":" + Key(keyLength, 128);
        var node = Import(method, password); Assert.True(node.IsSupported);
        var result = XrayConfigCompiler.Compile(Policy(node), PolicyTests.Options()); Assert.True(result.IsSuccess);
        using var json = JsonDocument.Parse(result.Json!);
        var outbound = json.RootElement.GetProperty("outbounds").EnumerateArray().Single(x => x.GetProperty("tag").GetString() == node.Id);
        Assert.Equal(password, outbound.GetProperty("settings").GetProperty("password").GetString());
    }

    [Theory]
    [InlineData("2022-blake3-aes-128-gcm", 16)]
    [InlineData("2022-blake3-aes-256-gcm", 32)]
    [InlineData("2022-blake3-chacha20-poly1305", 32)]
    public void InvalidEncodingLengthsAndChainSegmentsCannotCompile(string method, int keyLength)
    {
        string valid = Key(keyLength);
        string[] invalid = ["not-base64", new string('x', keyLength), Key(keyLength - 1), Key(keyLength + 1), valid.TrimEnd('='), " " + valid, valid + "\n", valid + ":", ":" + valid, valid + ":bad-key", Convert.ToBase64String(Enumerable.Repeat((byte)255, keyLength).ToArray()).Replace('/', '_')];
        foreach (var password in invalid)
        {
            // Test the domain/compiler boundary directly so YAML whitespace normalization cannot hide invalid bytes.
            var direct = Import(method, valid); direct.Password = password;
            var compiled = XrayConfigCompiler.Compile(Policy(direct), PolicyTests.Options());
            Assert.False(compiled.IsSuccess); Assert.Null(compiled.Json); Assert.Contains(compiled.Diagnostics, x => x.Code == "node.ss2022-key");
        }
        foreach (var password in new[] { "not-base64", Key(keyLength - 1), valid + ":bad-key" })
        {
            var imported = Import(method, password); Assert.False(imported.IsSupported); Assert.Contains(imported.Diagnostics, x => x.Code == "node.ss2022-key");
        }
    }

    [Fact]
    public void ChaChaColonChainIsExplicitlyUnsupportedEvenWithValidKeys()
    {
        var node = Import("2022-blake3-chacha20-poly1305", Key(32) + ":" + Key(32, 64));
        Assert.False(node.IsSupported); Assert.False(XrayConfigCompiler.Compile(Policy(node), PolicyTests.Options()).IsSuccess);
    }

    [Fact]
    public void InvalidUnselected2022NodeDoesNotBlockAnotherValidProxy()
    {
        var policy = PolicyTests.Document(); policy.Nodes.Add(Import("2022-blake3-aes-256-gcm", "not-base64"));
        Assert.True(XrayConfigCompiler.Compile(policy, PolicyTests.Options()).IsSuccess);
    }

    private static ProxyNode Import(string method, string password)
        => Assert.Single(ClashSubscriptionImporter.Import("proxies: [{name: synthetic, type: ss, server: proxy.example.test, port: 443, cipher: " + method + ", password: '" + password + "'}]", "synthetic-ss2022").Nodes);
    private static MacPolicyDocument Policy(ProxyNode node)
    {
        var policy = PolicyTests.Document(); policy.Nodes = [node]; policy.Groups.Clear(); policy.RuleSets.Clear(); PolicyTests.SelectGlobalNode(policy, node.Id); return policy;
    }
    private static string Key(int length, int offset = 0) => Convert.ToBase64String(Enumerable.Range(offset, length).Select(value => (byte)value).ToArray());
}
