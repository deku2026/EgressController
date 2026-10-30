// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using EgressController.Mac;

namespace EgressController.Mac.Tests;

public sealed class HelperProtocolTests
{
    private static readonly Guid Session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly MacAuthenticatedPeer Peer = new(501, 1234, 5678, true);
    private static MacHelperRequest Apply => new(1, Session, 2, MacHelperOperation.ApplyValidatedConfiguration,
        "/owned/config/current.json", new string('a', 64), "/owned/cores/v1/xray", new string('b', 64),
        "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222");
    private static string? Check(MacHelperRequest request, MacAuthenticatedPeer? peer = null)
        => MacHelperRequestValidator.Validate(request, peer ?? Peer, 501, "/owned/config", "/owned/cores", Session, 1);

    [Fact]
    public void OnlyAuthenticatedFreshBoundedOperationsAreAccepted()
    {
        Assert.Null(Check(Apply));
        Assert.Equal("unauthenticated-peer", Check(Apply, Peer with { UserId = 502 }));
        Assert.Equal("unauthenticated-peer", Check(Apply, Peer with { ExecutableIdentityVerified = false }));
        Assert.Equal("invalid-session", Check(Apply with { SessionId = Guid.NewGuid() }));
        Assert.Equal("stale-sequence", Check(Apply with { Sequence = 1 }));
        Assert.Equal("outside-owned-directory", Check(Apply with { ConfigurationPath = "/owned/config/../etc/resolv.conf" }));
        Assert.Equal("outside-owned-directory", Check(Apply with { ConfigurationPath = "/owned/config-other/current.json" }));
        Assert.Equal("invalid-content-digest", Check(Apply with { XraySha256 = "not-a-digest" }));
        Assert.Equal("invalid-interface-identity", Check(Apply with { NonHomeServiceId = Apply.HomeServiceId }));
        Assert.Equal("unexpected-payload", Check(Apply with { Operation = MacHelperOperation.StopAndRestore }));
    }

    [Fact]
    public void ContentHashBindsConfigurationBytesAndSerializationIsAotGenerated()
    {
        byte[] content = Encoding.UTF8.GetBytes("{}\n");
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        Assert.True(MacHelperRequestValidator.ContentMatches(content, digest));
        Assert.False(MacHelperRequestValidator.ContentMatches(Encoding.UTF8.GetBytes("{ }\n"), digest));
        string json = JsonSerializer.Serialize(Apply, MacHelperJson.Default.MacHelperRequest);
        Assert.Equal(Apply, JsonSerializer.Deserialize(json, MacHelperJson.Default.MacHelperRequest));
    }

    [Fact]
    public void DnsRecoveryDistinguishesAbsentAndExplicitConfigurationsAndPreservesBytes()
    {
        string plist = Convert.ToBase64String(Encoding.UTF8.GetBytes("<plist version=\"1.0\"><dict><key>ServerAddresses</key><array/></dict></plist>"));
        var snapshot = new MacDnsRecoverySnapshot(1, Session, DateTimeOffset.UnixEpoch,
            [new(Apply.HomeServiceId!, true, false, plist), new(Apply.NonHomeServiceId!, false, false, null)]);
        snapshot.Validate();
        string json = JsonSerializer.Serialize(snapshot, MacHelperJson.Default.MacDnsRecoverySnapshot);
        var restored = JsonSerializer.Deserialize(json, MacHelperJson.Default.MacDnsRecoverySnapshot)!;
        Assert.Equal(plist, restored.Services[0].OriginalConfigurationPlistBase64);
        Assert.False(restored.Services[1].ProtocolWasPresent);
        Assert.Throws<ArgumentException>(() => (snapshot with { Services = [new(Apply.HomeServiceId!, false, true, null)] }).Validate());
        Assert.Throws<ArgumentException>(() => (snapshot with { Services = [new(Apply.HomeServiceId!, true, true, "invalid base64")] }).Validate());
    }

    [Fact]
    public void HelperFramingRejectsTruncationTrailingDataAndOversizedClaims()
    {
        byte[] frame = MacHelperFrameCodec.Encode(Apply);
        Assert.Equal(Apply, MacHelperFrameCodec.DecodeRequest(frame));
        Assert.Throws<InvalidDataException>(() => MacHelperFrameCodec.DecodeRequest(frame.AsSpan(0, 3)));
        Assert.Throws<InvalidDataException>(() => MacHelperFrameCodec.DecodeRequest(frame.AsSpan(0, frame.Length - 1)));
        Assert.Throws<InvalidDataException>(() => MacHelperFrameCodec.DecodeRequest(frame.Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<InvalidDataException>(() => MacHelperFrameCodec.DecodeRequest(new byte[] { 0x7f, 0xff, 0xff, 0xff }));
    }
}
