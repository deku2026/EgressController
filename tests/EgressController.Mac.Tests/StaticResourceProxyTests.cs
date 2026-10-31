// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.Mac.Tests;

public sealed class StaticResourceProxyTests
{
    private static readonly Uri Destination = new("https://resource.example.test/data?token=synthetic-private");
    private static ReadOnlyMemory<byte> Json(string value) => Encoding.UTF8.GetBytes(value);

    [Theory]
    [InlineData("http", "http")]
    [InlineData("https", "http")]
    [InlineData("socks", "socks5")]
    public void StaticDecisionPinsItsFirstProxyAndDoesNotAllowTheLaterDirectFallback(string nativeType, string expectedScheme)
    {
        var proxy = MacStaticProxyDecision.Decode(Json($$"""{"proxies":[{"type":"{{nativeType}}","host":"127.0.0.1","port":7890},{"type":"direct"}]}"""));
        Assert.NotNull(proxy); Assert.False(proxy.IsBypassed(Destination));
        var address = proxy.GetProxy(Destination)!;
        Assert.Equal(expectedScheme, address.Scheme); Assert.Equal("127.0.0.1", address.Host); Assert.Equal(7890, address.Port);
    }

    [Fact]
    public void DirectRequiresAnExplicitNoneDecisionRatherThanAnEmptyOrMissingResult()
    {
        Assert.Null(MacStaticProxyDecision.Decode(Json("{\"proxies\":[{\"type\":\"direct\"}]}")));
        Assert.Throws<ResourceProxyConfigurationException>(() => MacStaticProxyDecision.Decode(Json("{\"proxies\":[]}")));
        Assert.Throws<ResourceProxyConfigurationException>(() => MacStaticProxyDecision.Decode(Json("{}")));
        Assert.Throws<ResourceProxyConfigurationException>(() => MacStaticProxyDecision.Decode(Json("{\"proxies\":[{\"type\":\"direct\",\"host\":\"hidden.test\"}]}")));
    }

    [Theory]
    [InlineData("{\"proxies\":[{\"type\":\"pac\"},{\"type\":\"direct\"}]}")]
    [InlineData("{\"proxies\":[{\"type\":\"unsupported\"},{\"type\":\"direct\"}]}")]
    [InlineData("{\"proxies\":[{\"type\":\"http\",\"host\":\"user:secret@proxy.test\",\"port\":7890}]}")]
    [InlineData("{\"proxies\":[{\"type\":\"http\",\"host\":\"proxy.test/path\",\"port\":7890}]}")]
    [InlineData("{\"proxies\":[{\"type\":\"http\",\"host\":\"proxy.test\",\"port\":0}]}")]
    [InlineData("{\"proxies\":[{\"type\":\"http\",\"host\":\"proxy.test\",\"port\":65536}]}")]
    [InlineData("{\"proxies\":[{\"type\":\"http\",\"host\":\"proxy.test\",\"port\":\"7890\"}]}")]
    [InlineData("{\"proxies\":null}")]
    [InlineData("not JSON token=synthetic-private")]
    public void UnsupportedPacAndMalformedStaticSettingsFailClosedWithoutExposingRawInput(string snapshot)
    {
        var error = Assert.Throws<ResourceProxyConfigurationException>(() => MacStaticProxyDecision.Decode(Json(snapshot)));
        Assert.Equal(snapshot.Contains("\"pac\"", StringComparison.Ordinal) ? ResourceProxyFailureKind.PacUnsupported : ResourceProxyFailureKind.InvalidSettings, error.Reason);
        Assert.DoesNotContain("synthetic-private", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("未", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticCredentialsStayInProxyAuthenticationAndNeverInTheAddress()
    {
        var proxy = MacStaticProxyDecision.Decode(Json("{\"proxies\":[{\"type\":\"http\",\"host\":\"127.0.0.1\",\"port\":7890,\"username\":\"synthetic-user\",\"password\":\"synthetic-password\"}]}"))!;
        var address = proxy.GetProxy(Destination)!; Assert.Equal("", address.UserInfo);
        var credential = proxy.Credentials!.GetCredential(address, "Basic")!;
        Assert.Equal("synthetic-user", credential.UserName); Assert.Equal("synthetic-password", credential.Password);
        Assert.DoesNotContain("synthetic-password", new MacStaticProxyEntry("http", "127.0.0.1", 7890, "synthetic-user", "synthetic-password").ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https_proxy", "http://user:password@proxy.test:7890", "http")]
    [InlineData("HTTPS_PROXY", "https://proxy.test:7890", "https")]
    [InlineData("ALL_PROXY", "socks5://proxy.test:7890", "socks5")]
    [InlineData("all_proxy", "proxy.test:7890", "http")]
    public void ApplicableEnvironmentProxyOverridesNativeSnapshotWithoutDefaultProxyOrNativeCalls(string variable, string value, string expectedScheme)
    {
        var environment = new Dictionary<string, string?> { [variable] = value };
        var resolver = new NativeMacResourceProxyResolver(_ => throw new InvalidOperationException("Native should not be read."), key => environment.GetValueOrDefault(key));
        var proxy = resolver.Resolve(Destination)!; var address = proxy.GetProxy(Destination)!;
        Assert.Equal(expectedScheme, address.Scheme); Assert.Equal("", address.UserInfo);
    }

    [Theory]
    [InlineData("resource.example.test", "https://resource.example.test/data", true)]
    [InlineData("resource.example.test", "https://child.resource.example.test/data", false)]
    [InlineData(".example.test", "https://resource.example.test/data", true)]
    [InlineData(".example.test", "https://example.test/data", true)]
    [InlineData(".example.test", "https://another.test/data", false)]
    [InlineData("*", "https://resource.example.test/data", false)]
    public void EnvironmentBypassHonorsExactAndLeadingDotSemanticsBeforeFreezingTheDecision(string bypass, string destination, bool direct)
    {
        var environment = new Dictionary<string, string?> { ["HTTPS_PROXY"] = "http://127.0.0.1:7890", ["NO_PROXY"] = bypass };
        var resolver = new NativeMacResourceProxyResolver(_ => throw new InvalidOperationException("Native should not be read."), key => environment.GetValueOrDefault(key));
        Assert.Equal(direct, resolver.Resolve(new(destination)) is null);
    }

    [Theory]
    [InlineData("ftp://proxy.test:7890")]
    [InlineData("http://proxy.test:invalid")]
    [InlineData("http://proxy.test:7890/path")]
    [InlineData("http://proxy.test:7890?token=synthetic-private")]
    [InlineData("http://proxy.test:7890\r\nprivate")]
    public void MalformedEnvironmentOverrideCannotFallBackToSystemOrDirect(string value)
    {
        var environment = new Dictionary<string, string?> { ["https_proxy"] = value, ["HTTPS_PROXY"] = "http://valid.test:7890" };
        var resolver = new NativeMacResourceProxyResolver(_ => throw new InvalidOperationException("No fallback allowed."), key => environment.GetValueOrDefault(key));
        var error = Assert.Throws<ResourceProxyConfigurationException>(() => resolver.Resolve(Destination));
        Assert.DoesNotContain("synthetic-private", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HttpOnlyEnvironmentDoesNotOverrideAnHttpsSystemDecisionAndReadFailuresAreSanitized()
    {
        int reads = 0;
        var resolver = new NativeMacResourceProxyResolver(_ => { reads++; return Json("{\"proxies\":[{\"type\":\"socks\",\"host\":\"127.0.0.1\",\"port\":7890}]}"); },
            key => key == "HTTP_PROXY" ? "http://not-for-https.test:7890" : null);
        Assert.Equal("socks5", resolver.Resolve(Destination)!.GetProxy(Destination)!.Scheme); Assert.Equal(1, reads);
        var failed = new NativeMacResourceProxyResolver(_ => throw new IOException("token=synthetic-private"), _ => null);
        Assert.DoesNotContain("synthetic-private", Assert.Throws<ResourceProxyConfigurationException>(() => failed.Resolve(Destination)).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitEmptyLowercaseBypassOverridesTheUppercaseBypassList()
    {
        var environment = new Dictionary<string, string?> { ["HTTPS_PROXY"] = "http://127.0.0.1:7890", ["NO_PROXY"] = "resource.example.test", ["no_proxy"] = "" };
        var resolver = new NativeMacResourceProxyResolver(_ => throw new InvalidOperationException("Native should not be read."), key => environment.GetValueOrDefault(key));
        Assert.NotNull(resolver.Resolve(Destination));
    }

    [Fact]
    public void SnapshotCapacityAndDefaultGateAreEnforcedBeforeLiveStateCanBeRead()
    {
        Assert.Throws<ResourceProxyConfigurationException>(() => MacStaticProxyDecision.Decode(new byte[MacStaticProxyDecision.MaximumSnapshotBytes + 1]));
        Assert.Throws<InvalidOperationException>(() => new NativeMacResourceProxyResolver(new LiveOperationGate()).Resolve(Destination));
    }

    [Fact]
    public void EnvironmentKeysAreCapturedOnceAcrossRedirectDestinationsIncludingBypassDecisions()
    {
        var values = new Dictionary<string, string?> { ["HTTPS_PROXY"] = "http://first.test:7890", ["NO_PROXY"] = "bypass.example.test" };
        var reads = new Dictionary<string, int>(StringComparer.Ordinal); int native = 0;
        var resolver = new NativeMacResourceProxyResolver(_ => { native++; return Json("{\"proxies\":[{\"type\":\"direct\"}]}"); }, key =>
        { reads[key] = reads.GetValueOrDefault(key) + 1; return values.GetValueOrDefault(key); });
        Assert.Empty(reads); // Constructing a resolver or a cache hit must not capture anything.
        Assert.Equal("first.test", resolver.Resolve(Destination)!.GetProxy(Destination)!.Host);
        values["HTTPS_PROXY"] = "http://changed.test:9999"; values["NO_PROXY"] = "resource.example.test";
        var redirect = new Uri("https://redirect.example.test/body");
        Assert.Equal("first.test", resolver.Resolve(redirect)!.GetProxy(redirect)!.Host);
        Assert.Null(resolver.Resolve(new("https://bypass.example.test/body")));
        Assert.NotNull(resolver.Resolve(Destination));
        Assert.Equal(0, native); Assert.Equal(6, reads.Count); Assert.All(reads.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void NativeSettingsAreCapturedOnceDetachedAndReusedForDestinationSpecificDecisions()
    {
        byte[] source = [1]; int captures = 0, resolutions = 0;
        var resolver = new NativeMacResourceProxyResolver(() => { captures++; return source; }, (destination, snapshot) =>
        {
            resolutions++; Assert.Equal((byte)1, snapshot.Span[0]);
            return destination.Host == "bypass.example.test" ? Json("{\"proxies\":[{\"type\":\"direct\"}]}")
                : Json("{\"proxies\":[{\"type\":\"http\",\"host\":\"captured.test\",\"port\":7890}]}");
        }, _ => null);
        Assert.Equal(0, captures);
        Assert.Equal("captured.test", resolver.Resolve(Destination)!.GetProxy(Destination)!.Host);
        source[0] = 2; // A mutable source settings object cannot change the captured bytes.
        Assert.Null(resolver.Resolve(new("https://bypass.example.test/source")));
        Assert.Equal("captured.test", resolver.Resolve(new("https://redirect.example.test/body"))!.GetProxy(Destination)!.Host);
        Assert.Equal(1, captures); Assert.Equal(3, resolutions);
    }

    [Fact]
    public void LegacyStaticFixtureDecisionIsAlsoCapturedOnceAndNeverSwitchesToDirect()
    {
        int captures = 0;
        byte[] source = Json("{\"proxies\":[{\"type\":\"socks\",\"host\":\"captured.test\",\"port\":7890}]}").ToArray();
        var resolver = new NativeMacResourceProxyResolver(_ => { captures++; return source; }, _ => null);
        Assert.NotNull(resolver.Resolve(Destination)); Array.Fill(source, (byte)' ');
        Assert.Equal("socks5", resolver.Resolve(new("https://redirect.example.test/body"))!.GetProxy(Destination)!.Scheme);
        Assert.Equal(1, captures);
    }
}
