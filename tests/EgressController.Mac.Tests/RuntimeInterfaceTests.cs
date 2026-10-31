using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.Mac.Tests;

public sealed class RuntimeInterfaceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RuntimeBindingIdentityDoesNotRequireLinksOrIpAddresses(bool homeOnline, bool nonHomeOnline)
    {
        MacNetworkInterface[] interfaces = [Interface("home", "en7", homeOnline), Interface("other", "en0", nonHomeOnline)];
        Assert.Equal(new MacRuntimeInterfaces("en7", "en0"), MacRuntimeInterfaces.Resolve(interfaces, "home", "other"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("same")]
    [InlineData("virtual")]
    public void MissingOrAmbiguousIdentityNeverSubstitutesAnotherOnlineInterface(string mutation)
    {
        var interfaces = new List<MacNetworkInterface> { Interface("home", "en7", false), Interface("other", "en0", false), Interface("unselected", "en9", true) };
        if (mutation == "missing") interfaces.RemoveAt(0);
        if (mutation == "duplicate") interfaces.Add(Interface("home", "en8", true));
        if (mutation == "same") interfaces[0] = Interface("home", "en0", false);
        if (mutation == "virtual") interfaces[0] = Interface("home", "utun8", false);
        Assert.Equal("interface.identity", Assert.Throws<RuntimeFailureException>(() => MacRuntimeInterfaces.Resolve(interfaces, "home", "other")).Code);
    }

    private static MacNetworkInterface Interface(string id, string bsd, bool online) => new(id, bsd, id, "Ethernet", null, online, []);
}
