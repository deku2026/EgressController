using System.Diagnostics;
using EgressController.Core.Routing;
using EgressController.Core.Storage;
using EgressController.Core.Protection;
using EgressController.Surge;
using EgressController.Transport.Upstream;

namespace EgressController.Mac;

public sealed class MacWorkspace(string directory) : IMacWorkspace
{
    private readonly MacDiscovery _discovery = new();
    private readonly KeychainSecretStore _secrets = new();
    private RoutingProfile _profile = new();
    private MacSettings _settings = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public bool IsPreview => false;
    public string DirectoryPath => directory;
    public async Task<WorkspaceSnapshot> LoadAsync(CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        _profile = await PrivateFile.ReadJsonAsync(Path.Combine(directory, "routing.json"), RoutingJsonContext.Default.RoutingProfile, token).ConfigureAwait(false) ?? new();
        if (_profile.SchemaVersion != 1 || _profile.Platform != "macos") throw new InvalidDataException("不支持的配置版本；原文件已保留。");
        _settings = await PrivateFile.ReadJsonAsync(Path.Combine(directory, "settings.json"), MacSettingsJson.Default.MacSettings, token).ConfigureAwait(false) ?? new();
        var adapters = await RefreshAdaptersAsync(token).ConfigureAwait(false);
        var apps = await RefreshApplicationsAsync(token).ConfigureAwait(false);
        return new(_profile, _settings, adapters, apps);
    }
    public Task<IReadOnlyList<ResolvedAdapter>> RefreshAdaptersAsync(CancellationToken token) => _discovery.AdaptersAsync(token);
    public Task<IReadOnlyList<MacApplication>> RefreshApplicationsAsync(CancellationToken token) => _discovery.ApplicationsAsync(token);
    public Task<MacApplication> InspectApplicationAsync(string path, CancellationToken token) => _discovery.InspectAsync(path, token);
    public Task<SocksStatus> ProbeAsync(int port, CancellationToken token) => new SocksProbe().CheckAsync(port, token);
    public async Task SaveAsync(RoutingProfile profile, CancellationToken token)
    {
        await _saveGate.WaitAsync(token).ConfigureAwait(false);
        try { await PrivateFile.WriteJsonAsync(Path.Combine(directory, "routing.json"), profile, RoutingJsonContext.Default.RoutingProfile, token).ConfigureAwait(false); _profile = profile; }
        finally { _saveGate.Release(); }
    }
    public async Task<PreparedConfiguration> PrepareAsync(RoutingProfile profile, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        foreach (var proxy in profile.Ports)
        {
            var observed = await ProbeAsync(proxy.Port, token).ConfigureAwait(false);
            if (!observed.Socks5 || observed.OwnerPath.Length == 0 || observed.OwnerPath != proxy.OwnerPath)
                throw new InvalidDataException($"端口 {proxy.Port} 状态或所属核心已改变；请重新检测并确认出口。");
        }
        var adapters = await RefreshAdaptersAsync(token).ConfigureAwait(false);
        var source = await ReadSubscriptionAsync(token).ConfigureAwait(false);
        string content = new SurgeConfigCompiler().Compile(profile, adapters, source?.ProxySection);
        var prepared = await new ManagedConfiguration(Path.Combine(directory, "profiles"), new SurgeCli(_settings.SurgeCliPath)).PrepareAsync(content, token).ConfigureAwait(false);
        Log(prepared.Validation.Valid ? "配置校验通过；等待手动导入 Surge" : "配置校验失败；保留原网络配置");
        return prepared;
    }
    public Task<string> RestoreAsync(CancellationToken token) => new ManagedConfiguration(Path.Combine(directory, "profiles"), new SurgeCli(_settings.SurgeCliPath)).RestoreCandidateAsync(token);
    public async Task<SurgeStatus> CheckSurgeAsync(MacSettings settings, string? key, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        using var api = new SurgeHttpApi(new Uri(settings.SurgeEndpoint), string.IsNullOrEmpty(key) ? _secrets.Read("surge-api") ?? "" : key);
        var status = await api.CheckAsync(token).ConfigureAwait(false); Log(status.Summary); return status;
    }
    public async Task SaveSettingsAsync(MacSettings settings, string? surgeKey, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        using var validate = new SurgeHttpApi(new Uri(settings.SurgeEndpoint), "");
        if (!string.IsNullOrEmpty(surgeKey)) _secrets.Write("surge-api", surgeKey);
        await PrivateFile.WriteJsonAsync(Path.Combine(directory, "settings.json"), settings, MacSettingsJson.Default.MacSettings, token).ConfigureAwait(false);
        _settings = settings;
    }
    public Task<IReadOnlyList<SurgeRule>> RulesAsync(bool refresh, CancellationToken token) => new SurgeRuleCatalog(
        new DefaultProxyDownloadTransport(() => _profile.DefaultDownloadPort), Path.Combine(directory, "cache", "surge-catalog.json")).LoadAsync(refresh, token);
    public async Task<EgressController.Subscriptions.SurgeSource?> ReadSubscriptionAsync(CancellationToken token)
    {
        string path = Path.Combine(directory, "subscriptions", "latest.conf");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("订阅文件过大。");
        return EgressController.Subscriptions.SurgeSource.Parse(await File.ReadAllTextAsync(path, new System.Text.UTF8Encoding(false, true), token).ConfigureAwait(false));
    }
    public Task OpenLogsAsync()
    {
        RuntimeSafety.RequireLiveOperations(); string logs = Path.Combine(directory, "logs"); PrivateFile.EnsureDirectory(logs);
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add(logs); using var process = Process.Start(start); return Task.CompletedTask;
    }
    private void Log(string message)
    {
        try
        {
            string logs = Path.Combine(directory, "logs"); PrivateFile.EnsureDirectory(logs); string path = Path.Combine(logs, "manager.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Logs have no authority over validation/status. */ }
    }
}
