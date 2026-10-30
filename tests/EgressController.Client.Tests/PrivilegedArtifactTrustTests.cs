using EgressController.Mac;
using EgressController.MacHelper;
using EgressController.Runtime;

namespace EgressController.Client.Tests;

public sealed class PrivilegedArtifactTrustTests
{
    [Fact]
    public void SelfReportedCoreHashCannotAuthorizeAnArbitraryPrivilegedExecutable()
    {
        var pinned = Installation(XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256);
        HelperInstallation.Validate(pinned);
        var arbitrary = pinned with { XraySha256 = new string('a', 64) };
        Assert.Throws<InvalidDataException>(() => HelperInstallation.Validate(arbitrary));
    }
    [Theory]
    [InlineData(1, 501u)]
    [InlineData(2, 0u)]
    public void UnknownVersionOrRootGuiIdentityCannotBeInstalled(int version, uint uid)
    {
        var identity = Installation(XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256) with { Version = version, AuthorizedUserId = uid };
        Assert.Throws<InvalidDataException>(() => HelperInstallation.Validate(identity));
    }
    private static MacHelperInstallation Installation(string coreHash) => new(2, 501, 20,
        "/fixture/Egress.app/Contents/MacOS/EgressController.Desktop", new string('b', 64), "/fixture/private/staging", coreHash,
        new string('c', 64), new string('d', 64));
}
