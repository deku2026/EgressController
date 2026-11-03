using EgressController.Core.Routing;
using EgressController.Mac;
using EgressController.Surge;

namespace EgressController.MacApp.Preview;

public sealed class MockWorkspace(string scenario = "ready") : IMacWorkspace
{
    public bool IsPreview => true;
    public string DirectoryPath => "预览：仅在内存中保存";
    private RoutingProfile _profile = new()
    {
        Adapters = [new("demo-wifi", "Wi-Fi · 家宽"), new("demo-usb", "USB · 第二出口")],
        Ports = [new(7890, "本地代理", "/Applications/FlClash.app/Contents/MacOS/mihomo", "demo-usb")],
        DefaultDownloadPort = 7890,
        Applications = [new("com.anthropic.claudefordesktop", "Claude", "/Applications/Claude.app/", true, RouteDestination.Socks(7890), "AI"), new("com.openai.chat", "ChatGPT", "/Applications/ChatGPT.app/", true, RouteDestination.Adapter("demo-wifi"), "AI")],
        Domains = [new("anthropic.com", RouteDestination.Socks(7890)), new("apple.com", new())],
        RuleSets = [new("115", "115", "https://raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/rule/Surge/115/115.list", new())]
    };
    public Task<WorkspaceSnapshot> LoadAsync(CancellationToken token) => Task.FromResult(new WorkspaceSnapshot(scenario == "empty" ? new() : _profile, new(), Adapters(), Apps()));
    private ResolvedAdapter[] Adapters() => [new("demo-wifi", "Wi-Fi", "en0", true, "192.0.2.24"), new("demo-usb", "USB 共享", "en7", scenario != "offline", scenario == "offline" ? "未连接" : "198.51.100.10")];
    private static MacApplication[] Apps() => [new("com.anthropic.claudefordesktop", "Claude", "/Applications/Claude.app/", true, null), new("com.openai.chat", "ChatGPT", "/Applications/ChatGPT.app/", true, null), new("chrome", "Google Chrome", "/Applications/Google Chrome.app/", true, null), new("code", "Visual Studio Code", "/Applications/Visual Studio Code.app/", true, null), new("telegram", "Telegram", "/Applications/Telegram.app/", true, null), new("spotify", "Spotify", "/Applications/Spotify.app/", true, null), new("cli", "Claude CLI", "/Users/demo/.local/bin/claude", false, null), new("safari", "Safari", "/Applications/Safari.app/", true, null)];
    public Task<IReadOnlyList<ResolvedAdapter>> RefreshAdaptersAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ResolvedAdapter>>(Adapters());
    public Task<IReadOnlyList<MacApplication>> RefreshApplicationsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MacApplication>>(Apps());
    public Task<MacApplication> InspectApplicationAsync(string path, CancellationToken token) => Task.FromResult(new MacApplication(path, Path.GetFileNameWithoutExtension(path), path.TrimEnd('/') + "/", true, null));
    public Task<SocksStatus> ProbeAsync(int port, CancellationToken token) => Task.FromResult(new SocksStatus(port, true, true, "/Applications/FlClash.app/Contents/MacOS/mihomo", "SOCKS5 握手正常 · 模拟"));
    public Task SaveAsync(RoutingProfile profile, CancellationToken token) { _profile = profile; return Task.CompletedTask; }
    public Task<PreparedConfiguration> PrepareAsync(RoutingProfile profile, CancellationToken token)
    {
        if (scenario == "failed") return Task.FromResult(new PreparedConfiguration("# 模拟无效配置", "", new(false, "Surge 校验失败 · 上次有效配置已保留")));
        string content = new SurgeConfigCompiler().Compile(profile, Adapters());
        return Task.FromResult(new PreparedConfiguration(content, "demo-validated", new(true, "校验通过 · 待手动应用")));
    }
    public Task<string> RestoreAsync(CancellationToken token) => Task.FromResult(new SurgeConfigCompiler().Compile(_profile, Adapters()));
    public Task<SurgeStatus> CheckSurgeAsync(MacSettings settings, string? key, CancellationToken token) => Task.FromResult(new SurgeStatus(scenario != "offline", scenario == "offline" ? "Surge API 暂不可用" : "Surge 已连接", DateTimeOffset.UtcNow));
    public Task SaveSettingsAsync(MacSettings settings, string? surgeKey, CancellationToken token) => Task.CompletedTask;
    public Task<IReadOnlyList<SurgeRule>> RulesAsync(bool refresh, CancellationToken token) => Task.FromResult<IReadOnlyList<SurgeRule>>(new[] { "115", "Apple", "Claude", "OpenAI", "Google", "Microsoft", "Telegram", "Spotify", "Steam", "YouTube", "Netflix" }.Select(n => new SurgeRule(n, n, $"https://raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/rule/Surge/{n}/{n}.list")).ToArray());
    public Task<EgressController.Subscriptions.SurgeSource?> ReadSubscriptionAsync(CancellationToken token) => Task.FromResult<EgressController.Subscriptions.SurgeSource?>(null);
    public Task OpenLogsAsync() => Task.CompletedTask;
}
