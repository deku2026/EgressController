namespace EgressController.Core.Tests;

/// <summary>Trivial machine-specific smoke to prove the unit-test pipeline itself is green.</summary>
public class SmokeTests
{
    [Fact]
    public void True_is_true()
        => Assert.True(true);

    [Fact]
    public void Egress_enum_has_exactly_two_egresses()
    {
        Assert.Equal(2, Enum.GetNames<Core.Routing.Egress>().Length);
        Assert.True(Enum.IsDefined(Core.Routing.Egress.Esim));
        Assert.True(Enum.IsDefined(Core.Routing.Egress.UpstreamProxy));
    }
}