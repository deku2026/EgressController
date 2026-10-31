using EgressController.Desktop.Production;
using EgressController.Desktop.Services;
using EgressController.Desktop.ViewModels;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Client.Tests;

public sealed class ConnectionPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Generation = new('a', 64);
    private static MacPolicyDocument Policy() => new()
    {
        Nodes = [new() { Id = "node-1", Name = "Example proxy", Protocol = NodeProtocol.Shadowsocks, Cipher = "aes-128-gcm", Password = "synthetic", Server = "proxy.example.invalid", Port = 443 }],
        Applications = [new() { Id = "editor", Name = "Example Editor", BundlePath = "/Applications/Example.app", ExecutablePaths = ["/Applications/Example.app/Contents/MacOS/Example"], Via = ExitChoice.Home }],
        RuleSets = [new() { Id = "service", Name = "Example service", Via = ExitChoice.NonHome }]
    };
    private static CoreConnectionSnapshot Snapshot(string? rule = "app-editor") => new(
        [new("session-1", Now) { Host = "api.example.invalid", Transport = "tcp", RemotePort = 443, MatchedRule = rule, OutboundTag = "node-1" }], Now,
        Limitation: CoreConnectionSnapshots.StockCoreLimitation) { GenerationId = Generation };

    [Fact]
    public void Actual_generation_labels_are_frozen_and_draft_edits_cannot_rewrite_history()
    {
        var policy = Policy(); var mapper = new ConnectionObservationMapper(); mapper.RememberApplied(Generation, policy);
        var first = Assert.Single(mapper.Map(Snapshot(), Now).Records);
        Assert.Equal("Example Editor", first.ApplicationName); Assert.Equal(ExitKind.Proxy, first.ObservedExitKind);
        policy.Nodes[0].Name = "Renamed in draft"; policy.Applications[0].Name = "Draft editor"; policy.Applications[0].Via = ExitChoice.NonHome;
        mapper.RememberApplied(Generation, policy);
        var next = Assert.Single(mapper.Map(Snapshot(), Now).Records);
        Assert.Equal(first.ObservedTargetName, next.ObservedTargetName); Assert.Equal(first.ApplicationName, next.ApplicationName);
        Assert.Null(next.ProcessId); Assert.Null(next.ExecutablePath); Assert.Null(next.UploadedBytes);
        Assert.Null(next.ObservedInterface); Assert.Equal(ConnectionActivity.Unknown, next.Activity);
    }

    [Fact]
    public void Unknown_generations_cannot_inherit_current_draft_application_names()
    {
        var mapper = new ConnectionObservationMapper(); mapper.RememberApplied(Generation, Policy());
        var unrelated = Snapshot() with { GenerationId = new string('b', 64) };
        var record = Assert.Single(mapper.Map(unrelated, Now).Records);
        Assert.Null(record.ApplicationId); Assert.Null(record.ApplicationName);
        Assert.Null(record.ObservedExitKind);
        Assert.Equal("node-1", record.ObservedTargetName); Assert.Equal("app-editor", record.MatchedRule);
    }

    [Fact]
    public void A_stale_observer_preserves_history_without_claiming_current_availability()
    {
        var mapper = new ConnectionObservationMapper(); mapper.RememberApplied(Generation, Policy());
        var first = mapper.Map(Snapshot("domains-service"), Now);
        Assert.Equal("规则 · Example service", Assert.Single(first.Records).MatchedRule);
        var unavailable = mapper.Map(Snapshot() with { CapturedAt = Now.AddMinutes(-1) }, Now);
        Assert.False(unavailable.IsAvailable); Assert.Equal(first.Records, unavailable.Records);
        Assert.Contains("过期", unavailable.Limitation!, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_preparation_phases_can_fail_retry_and_complete_without_setting_network_health()
    {
        int retries = 0; var attempt = new ConnectionAttemptViewModel(() => retries++);
        attempt.Begin(); attempt.Observe(new(ConnectionAttemptStage.Resources, "读取缓存"));
        Assert.True(attempt.IsRunning); Assert.Equal("完成", attempt.Steps[0].State); Assert.Equal("进行中", attempt.Steps[1].State);
        attempt.Fail("系统代理不可用"); Assert.True(attempt.HasFailed); Assert.False(attempt.IsRunning);
        attempt.Observe(new(ConnectionAttemptStage.Core, "过期进度")); Assert.Equal("系统代理不可用", attempt.Detail);
        attempt.RetryCommand.Execute(null); Assert.Equal(1, retries);
        attempt.Begin(); attempt.Observe(new(ConnectionAttemptStage.Core, "验证缓存核心"));
        attempt.Observe(new(ConnectionAttemptStage.Validating, "较早阶段")); Assert.Equal("验证缓存核心", attempt.Detail);
        attempt.Complete(); Assert.False(attempt.HasFailed); Assert.All(attempt.Steps, item => Assert.Equal("完成", item.State));
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("blocked")]
    [InlineData("unrecognized-outbound")]
    public void Reserved_or_unknown_outbounds_are_not_reported_as_proxy_traffic(string tag)
    {
        var mapper = new ConnectionObservationMapper(); mapper.RememberApplied(Generation, Policy());
        var snapshot = Snapshot(); snapshot = snapshot with { Records = [snapshot.Records[0] with { OutboundTag = tag }] };
        Assert.Null(Assert.Single(mapper.Map(snapshot, Now).Records).ObservedExitKind);
    }

    [Fact]
    public void First_observed_exit_classification_is_retained_with_its_original_label()
    {
        var mapper = new ConnectionObservationMapper(); mapper.RememberApplied(Generation, Policy());
        var snapshot = Snapshot(); var before = Assert.Single(mapper.Map(snapshot, Now).Records);
        var changed = snapshot with { Records = [snapshot.Records[0] with { OutboundTag = "nonhome" }] };
        var after = Assert.Single(mapper.Map(changed, Now).Records);
        Assert.Equal(before.ObservedExitKind, after.ObservedExitKind); Assert.Equal(before.ObservedTargetName, after.ObservedTargetName);
    }

    [Fact]
    public void Metadata_only_publications_do_not_evict_the_still_running_generation()
    {
        var mapper = new ConnectionObservationMapper(); mapper.RememberApplied(Generation, Policy());
        mapper.Map(Snapshot(), Now);
        for (int index = 0; index < 12; index++) mapper.RememberApplied(index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), Policy());
        var later = Snapshot() with { Records = [Snapshot().Records[0] with { Id = "new-session" }] };
        var row = Assert.Single(mapper.Map(later, Now).Records);
        Assert.Equal("Example Editor", row.ApplicationName); Assert.Equal("Example proxy", row.ObservedTargetName);
    }
}
