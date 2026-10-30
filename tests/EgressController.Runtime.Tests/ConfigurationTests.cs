using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ConfigurationTests
{
    internal static MacPolicyDocument Policy() => new()
    {
        Interfaces = new() { HomeId = "home-service", NonHomeId = "other-service" },
        Defaults = new() { Global = ExitChoice.Proxy("node-1") },
        Nodes = [new() { Id = "node-1", Name = "Example", SourceId = "example", Server = "proxy.example.invalid", Port = 443,
            Protocol = NodeProtocol.Vless, UserId = "11111111-1111-1111-1111-111111111111", Tls = true }],
        Applications = [new() { Id = "app-1", Name = "Example App", BundlePath = "/Applications/Example.app",
            PrimaryExecutable = "/Applications/Example.app/Contents/MacOS/Example",
            ExecutablePaths = ["/Applications/Example.app/Contents/MacOS/Example", "/Applications/Example.app/Contents/Helpers/OldHelper"], Via = ExitChoice.Home }]
    };
    internal static XrayCompileOptions Options() => new()
    { Interfaces = [new("home-service", "en0"), new("other-service", "en5")] };
    internal static IReadOnlyDictionary<string, byte[]> Assets()
    {
        var bundle = GeoRuleCompiler.Compile([new() { Tag = "cn", Domains = [new(DomainMatchKind.Suffix, "example.invalid")], IpCidrs = ["192.0.2.0/24"] }]);
        return new Dictionary<string, byte[]> { ["geosite.dat"] = bundle.GeoSite, ["geoip.dat"] = bundle.GeoIp };
    }

    [Fact]
    public async Task MissingOrWrongDatTagsCannotApplyEvenWithAPreparedSourceMapping()
    {
        var core = new MockCoreRuntime(); var coordinator = new ConfigurationCoordinator(core, new InMemoryGenerationStore(), new BoundedLifecycleLog());
        var policy = Policy();
        Assert.Equal(ApplyDisposition.Rejected, (await coordinator.ApplyAsync(policy, Options(), new Dictionary<string, byte[]>(), "缺少资源", TestContext.Current.CancellationToken)).Disposition);
        var wrong = GeoRuleCompiler.Compile([new() { Tag = "wrong", Domains = [new(DomainMatchKind.Full, "example.invalid")], IpCidrs = ["192.0.2.0/24"] }]);
        var assets = new Dictionary<string, byte[]> { ["geosite.dat"] = wrong.GeoSite, ["geoip.dat"] = wrong.GeoIp };
        Assert.Equal(ApplyDisposition.Rejected, (await coordinator.ApplyAsync(policy, Options(), assets, "错误资源标签", TestContext.Current.CancellationToken)).Disposition);
        Assert.Equal(0, core.ApplyCount);
    }

    [Fact]
    public async Task IdenticalRuntimeDoesNotRestartForMetadataChange()
    {
        var core = new MockCoreRuntime(); var store = new InMemoryGenerationStore(); var log = new BoundedLifecycleLog();
        var coordinator = new ConfigurationCoordinator(core, store, log);
        var policy = Policy();
        var first = await coordinator.ApplyAsync(policy, Options(), Assets(), "首次应用", TestContext.Current.CancellationToken);
        Assert.True(first.Succeeded, string.Join(";", first.Diagnostics.Select(item => item.Message)));
        Assert.Equal(ApplyDisposition.Unchanged, (await coordinator.ApplyAsync(policy, Options(), Assets(), "重复应用", TestContext.Current.CancellationToken)).Disposition);
        policy.Applications[0].Name = "Renamed App";
        var metadata = await coordinator.ApplyAsync(policy, Options(), Assets(), "更新名称", TestContext.Current.CancellationToken);
        Assert.True(metadata.Succeeded);
        Assert.Equal(1, core.ApplyCount);
        Assert.Contains("Renamed App", (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.PolicyYaml);
        Assert.All(log.Events, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Cause)));
    }

    [Fact]
    public async Task FailedApplyKeepsOldRuntimeAndDesiredFixedInventory()
    {
        var core = new MockCoreRuntime(); var store = new InMemoryGenerationStore();
        var coordinator = new ConfigurationCoordinator(core, store, new BoundedLifecycleLog());
        var policy = Policy();
        Assert.True((await coordinator.ApplyAsync(policy, Options(), Assets(), "初始配置", TestContext.Current.CancellationToken)).Succeeded);
        string previous = coordinator.Applied!.Id;
        policy.Applications[0].Via = ExitChoice.NonHome;
        core.FailApply = true;
        var failure = await coordinator.ApplyAsync(policy, Options(), Assets(), "切换出口", TestContext.Current.CancellationToken);
        Assert.Equal(ApplyDisposition.Failed, failure.Disposition);
        Assert.Equal(previous, core.Current!.Id);
        Assert.Equal(previous, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
        Assert.Contains("OldHelper", coordinator.DesiredPolicyYaml!);
        Assert.Equal(1, core.RestoreCount);
    }

    [Fact]
    public async Task PersistedConfigurationIsNotMistakenForARunningCoreAfterColdStart()
    {
        var store = new InMemoryGenerationStore();
        var first = new ConfigurationCoordinator(new MockCoreRuntime(), store, new BoundedLifecycleLog());
        Assert.True((await first.ApplyAsync(Policy(), Options(), Assets(), "首次运行", TestContext.Current.CancellationToken)).Succeeded);
        var coldCore = new MockCoreRuntime();
        var restarted = new ConfigurationCoordinator(coldCore, store, new BoundedLifecycleLog());
        Assert.Equal(ApplyDisposition.Applied, (await restarted.ApplyAsync(Policy(), Options(), Assets(), "重新打开", TestContext.Current.CancellationToken)).Disposition);
        Assert.Equal(1, coldCore.ApplyCount);
    }

    [Fact]
    public async Task ExplicitStopPreservesSavedPolicyAndResumeAppliesTheSameRuntimeAgain()
    {
        var store = new InMemoryGenerationStore(); var core = new MockCoreRuntime();
        var coordinator = new ConfigurationCoordinator(core, store, new BoundedLifecycleLog());
        Assert.True((await coordinator.ApplyAsync(Policy(), Options(), Assets(), "启动模拟", TestContext.Current.CancellationToken)).Succeeded);
        string saved = (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id;
        Assert.True(await coordinator.StopAsync("手动停止", TestContext.Current.CancellationToken));
        Assert.Null(coordinator.Applied); Assert.Null(core.Current);
        Assert.Equal(saved, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
        Assert.Equal(ApplyDisposition.Applied, (await coordinator.ApplyAsync(Policy(), Options(), Assets(), "手动恢复", TestContext.Current.CancellationToken)).Disposition);
        Assert.Equal(2, core.ApplyCount);
    }

    [Fact]
    public async Task CancellationWithFailedRecoveryReportsUnavailableInsteadOfCanceled()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var log = new BoundedLifecycleLog();
        var coordinator = new ConfigurationCoordinator(new CancellationAndBrokenRollback(caller), new InMemoryGenerationStore(), log);
        var result = await coordinator.ApplyAsync(Policy(), Options(), Assets(), "取消测试", caller.Token);
        Assert.Equal(ApplyDisposition.Failed, result.Disposition);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "apply.rollback-failed");
        Assert.Contains(log.Events, entry => entry.Kind == "protection.unavailable");
        Assert.Null(coordinator.Applied);
    }

    [Fact]
    public async Task PublicationFailureAfterPointerChangeRestoresPreviousGeneration()
    {
        var core = new MockCoreRuntime(); var store = new FailingAfterPublishStore();
        var coordinator = new ConfigurationCoordinator(core, store, new BoundedLifecycleLog());
        var policy = Policy();
        Assert.True((await coordinator.ApplyAsync(policy, Options(), Assets(), "初始配置", TestContext.Current.CancellationToken)).Succeeded);
        var previous = coordinator.Applied;
        policy.Applications[0].Via = ExitChoice.NonHome; store.FailAfterPublish = true;
        Assert.Equal(ApplyDisposition.Failed, (await coordinator.ApplyAsync(policy, Options(), Assets(), "失败后恢复", TestContext.Current.CancellationToken)).Disposition);
        Assert.Equal(previous!.Id, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
        Assert.Equal(previous.Id, core.Current!.Id);
    }

    [Fact]
    public async Task FailedCoreRecoveryStillRestoresTheSavedGeneration()
    {
        var core = new BrokenRecoveryCore(); var store = new FailingAfterPublishStore(); var log = new BoundedLifecycleLog();
        var coordinator = new ConfigurationCoordinator(core, store, log);
        var policy = Policy();
        Assert.True((await coordinator.ApplyAsync(policy, Options(), Assets(), "初始配置", TestContext.Current.CancellationToken)).Succeeded);
        var previous = coordinator.Applied!;
        policy.Applications[0].Via = ExitChoice.NonHome;
        store.FailAfterPublish = true; core.FailRecovery = true;
        var failure = await coordinator.ApplyAsync(policy, Options(), Assets(), "发布与内核恢复失败", TestContext.Current.CancellationToken);
        Assert.Equal(ApplyDisposition.Failed, failure.Disposition);
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "apply.rollback-failed");
        Assert.Equal(previous.Id, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
        Assert.Null(coordinator.Applied);
        Assert.Contains(log.Events, entry => entry.Kind == "protection.unavailable");
    }

    [Fact]
    public async Task InvalidPolicyAndLogFailuresDoNotChangeProtection()
    {
        var core = new MockCoreRuntime();
        var coordinator = new ConfigurationCoordinator(core, new InMemoryGenerationStore(), new BrokenLog());
        Assert.True((await coordinator.ApplyAsync(Policy(), Options(), Assets(), "日志不可用", TestContext.Current.CancellationToken)).Succeeded);
        var invalid = Policy(); invalid.Interfaces.HomeId = invalid.Interfaces.NonHomeId;
        Assert.Equal(ApplyDisposition.Rejected, (await coordinator.ApplyAsync(invalid, Options(), Assets(), "无效网卡", TestContext.Current.CancellationToken)).Disposition);
        Assert.Equal(1, core.ApplyCount);
    }

    [Theory]
    [InlineData("../geoip.dat")]
    [InlineData("/tmp/geoip.dat")]
    [InlineData("nested/geosite.dat")]
    public async Task ArtifactsCannotEscapeGeneration(string name)
    {
        var core = new MockCoreRuntime(); var coordinator = new ConfigurationCoordinator(core, new InMemoryGenerationStore(), new BoundedLifecycleLog());
        var result = await coordinator.ApplyAsync(Policy(), Options(), new Dictionary<string, byte[]> { [name] = [1] }, "拒绝非法资源路径", TestContext.Current.CancellationToken);
        Assert.Equal(ApplyDisposition.Rejected, result.Disposition); Assert.Equal(0, core.ApplyCount);
    }

    private sealed class BrokenLog : ILifecycleLog { public void Append(LifecycleEvent entry) => throw new Exception("Injected log failure."); }
    private sealed class BrokenRecoveryCore : ICoreRuntime
    {
        public bool FailRecovery { get; set; }
        public Task ValidateAsync(RuntimeGeneration generation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyAsync(RuntimeGeneration generation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken)
        {
            if (FailRecovery) throw new IOException("Injected core recovery failure.");
            return Task.CompletedTask;
        }
    }
    private sealed class CancellationAndBrokenRollback(CancellationTokenSource caller) : ICoreRuntime
    {
        public Task ValidateAsync(RuntimeGeneration generation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyAsync(RuntimeGeneration generation, CancellationToken cancellationToken)
        { caller.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken) => throw new IOException("Injected recovery failure.");
    }
    private sealed class FailingAfterPublishStore : IRuntimeGenerationStore
    {
        private RuntimeGeneration? _current;
        public bool FailAfterPublish { get; set; }
        public Task<RuntimeGeneration?> ReadCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(_current);
        public Task PublishAsync(RuntimeGeneration generation, CancellationToken cancellationToken = default)
        { _current = generation; if (FailAfterPublish) throw new IOException("Injected fsync failure after pointer replacement."); return Task.CompletedTask; }
        public Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken = default)
        { _current = generation; return Task.CompletedTask; }
    }
}

public sealed class GenerationStorageTests
{
    [Theory]
    [InlineData("{\"Current\":null}", false)]
    [InlineData("{\"Files\":null}", true)]
    public async Task StructurallyInvalidMetadataUsesBackup(string invalid, bool manifest)
    {
        string directory = Path.Combine(Path.GetTempPath(), "egress-schema-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AtomicGenerationStore(directory);
            var first = Generation("one"); var second = Generation("two");
            await store.PublishAsync(first, TestContext.Current.CancellationToken);
            await store.PublishAsync(second, TestContext.Current.CancellationToken);
            string target = manifest ? Path.Combine(directory, "generations", second.Id, "manifest.json") : Path.Combine(directory, "current.json");
            if (manifest) invalid = "{\"Id\":\"" + second.Id + "\",\"Files\":null}";
            await File.WriteAllTextAsync(target, invalid, TestContext.Current.CancellationToken);
            Assert.Equal(first.Id, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public async Task CorruptionRecoversVerifiedPreviousGeneration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "egress-generation-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AtomicGenerationStore(directory);
            var first = Generation("one"); var second = Generation("two");
            await store.PublishAsync(first, TestContext.Current.CancellationToken); await store.PublishAsync(second, TestContext.Current.CancellationToken);
            Assert.Equal(second.Id, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
            await File.WriteAllTextAsync(Path.Combine(directory, "generations", second.Id, "geosite.dat"), "corrupt", TestContext.Current.CancellationToken);
            Assert.Equal(first.Id, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
            await File.WriteAllTextAsync(Path.Combine(directory, "current.json"), "corrupt pointer", TestContext.Current.CancellationToken);
            Assert.Equal(first.Id, (await store.ReadCurrentAsync(TestContext.Current.CancellationToken))!.Id);
            Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "generations"), ".preparing-*"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "generations", first.Id, "policy.yaml")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    private static RuntimeGeneration Generation(string value)
    {
        var assets = new Dictionary<string, byte[]> { ["geosite.dat"] = System.Text.Encoding.UTF8.GetBytes(value) };
        return new(ConfigurationCoordinator.GenerationIdentity(value, "{}", assets), value, "{}", assets);
    }
}
