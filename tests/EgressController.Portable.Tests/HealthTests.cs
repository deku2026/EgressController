using EgressController.Subscriptions;
namespace EgressController.Portable.Tests;
public sealed class HealthTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,0,0,0,TimeSpan.Zero);
    private static NodeObservation Node(string id, ProbeOutcome outcome) => new(id,"HK " + id,outcome,Now);
    [Fact] public void Unknown_stale_and_duplicate_samples_do_not_manufacture_failures()
    {
        var policy = new HealthPolicy { ConsecutiveRounds = 1, Conditions = [new("HK","all-timeout",0)] };
        var result = HealthEvaluator.Evaluate(policy,new(),[Node("a",ProbeOutcome.Timeout),Node("b",ProbeOutcome.Unknown),Node("a",ProbeOutcome.Success) with { At=Now.AddMinutes(-30) }],true,true,Now);
        Assert.False(result.Refresh); Assert.Equal(1,result.Known); Assert.Equal(1,result.Unknown);
    }
    [Fact] public void Percentage_uses_only_completed_nodes_and_requires_consecutive_rounds()
    {
        var nodes = new[] { Node("a",ProbeOutcome.Timeout),Node("b",ProbeOutcome.Success),Node("c",ProbeOutcome.Unknown) };
        var first = HealthEvaluator.Evaluate(new(),new(),nodes,true,true,Now); Assert.False(first.Refresh);
        var second = HealthEvaluator.Evaluate(new(),first.State,nodes,true,true,Now.AddMinutes(15)); Assert.True(second.Refresh);
        var cooldown = HealthEvaluator.Evaluate(new() { ConsecutiveRounds = 1 },second.State,nodes.Select(n=>n with {At=Now.AddMinutes(30)}).ToArray(),true,true,Now.AddMinutes(30)); Assert.False(cooldown.Refresh);
    }
    [Theory] [InlineData(false,true)] [InlineData(true,false)]
    public void Backend_and_network_failure_never_trigger_refresh(bool backend,bool network)
    { var result=HealthEvaluator.Evaluate(new(){ConsecutiveRounds=1},new(),[Node("a",ProbeOutcome.Timeout)],backend,network,Now); Assert.False(result.Refresh); Assert.Equal(0,result.State.ConsecutiveFailures); }
    [Fact] public void Empty_match_is_unknown() => Assert.False(HealthEvaluator.Evaluate(new(){ConsecutiveRounds=1,Conditions=[new("Japan","all-timeout",0)]},new(),[Node("a",ProbeOutcome.Timeout)],true,true,Now).Refresh);
}
