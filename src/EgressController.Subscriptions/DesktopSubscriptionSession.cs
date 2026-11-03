using EgressController.Core.Contracts;
using EgressController.Core.Storage;

namespace EgressController.Subscriptions;

public sealed class DesktopSubscriptionSession(string root, ISecretStore secrets, ISubscriptionProvider? provider = null,
    Func<SubscriptionSettings, CancellationToken, Task<string>>? health = null) : ISubscriptionSession
{
    private string DirectoryPath => Path.Combine(root, "subscriptions");
    private string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public bool IsPreview => false;
    private string LoginPath => Path.Combine(DirectoryPath, "session.json");
    public async Task<LoginState> ReadLoginStateAsync(CancellationToken token) =>
        await PrivateFile.ReadJsonAsync(LoginPath, LoginJson.Default.LoginState, token).ConfigureAwait(false) ?? new(LoginStatus.Unknown, default, "尚未完成首次登录");
    public async Task<LoginState> LoginAsync(SubscriptionSettings settings, IProgress<SubscriptionProgress> progress, CancellationToken token)
    {
        var credentials = new ProviderCredentials("", "");
        var interactive = provider as IInteractiveSubscriptionProvider ?? new TagSubscriptionProvider();
        var result = await interactive.LoginAsync(settings, credentials, Path.Combine(DirectoryPath, "chrome-profile"), progress, token).ConfigureAwait(false);
        return await SaveLoginStateAsync(result, token).ConfigureAwait(false);
    }
    public async Task<LoginState> CheckSessionAsync(SubscriptionSettings settings, CancellationToken token)
    {
        var interactive = provider as IInteractiveSubscriptionProvider ?? new TagSubscriptionProvider();
        LoginState result;
        try { result = await interactive.CheckSessionAsync(settings, Path.Combine(DirectoryPath, "chrome-profile"), token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Http.HttpRequestException)
        { result = new(LoginStatus.Unknown, DateTimeOffset.UtcNow, "暂时无法确认会话；浏览器或网络不可用，未判定为过期"); }
        return await SaveLoginStateAsync(result, token).ConfigureAwait(false);
    }
    private async Task<LoginState> SaveLoginStateAsync(LoginState state, CancellationToken token)
    {
        var previous = await ReadLoginStateAsync(token).ConfigureAwait(false);
        DateTimeOffset? verified = state.Status == LoginStatus.Valid ? state.CheckedAt : previous.LastVerifiedAt ?? (previous.Status == LoginStatus.Valid ? previous.CheckedAt : null);
        if (state.Status == LoginStatus.NotSignedIn && verified.HasValue)
            state = state with { Status = LoginStatus.Expired, Detail = "网站已返回登录页：保存的会话已失效，请手动重新登录" };
        state = state with { LastVerifiedAt = verified };
        await PrivateFile.WriteJsonAsync(LoginPath, state, LoginJson.Default.LoginState, token).ConfigureAwait(false);
        return state;
    }
    public async Task<SubscriptionSettings> LoadAsync(CancellationToken token) =>
        await PrivateFile.ReadJsonAsync(SettingsPath, SubscriptionJson.Default.SubscriptionSettings, token).ConfigureAwait(false) ?? new();
    public async Task SaveAsync(SubscriptionSettings settings, string username, string password, string ingressToken, CancellationToken token)
    {
        if (!Uri.TryCreate(settings.Website, UriKind.Absolute, out var website) || website.Scheme != "https" || website.Host != "tagss.pro") throw new ArgumentException("当前 Provider 仅支持 https://tagss.pro/。");
        if (!Path.IsPathFullyQualified(settings.ChromePath) || !File.Exists(settings.ChromePath)) throw new ArgumentException("Chrome 可执行文件不存在。");
        _ = new System.Text.RegularExpressions.Regex(settings.GroupPattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (settings.FailurePercent is < 1 or > 100 || settings.ConsecutiveRounds < 1 || settings.CooldownMinutes < 15) throw new ArgumentException("健康检测条件无效。");
        if (ingressToken.Length > 0)
        {
            using var validate = new GrpcSubscriptionUploader(new Uri(settings.ServerEndpoint), ingressToken);
            secrets.Write("subscription:ingress", ingressToken);
        }
        await PrivateFile.WriteJsonAsync(SettingsPath, settings, SubscriptionJson.Default.SubscriptionSettings, token).ConfigureAwait(false);
    }
    public async Task<SubscriptionResult[]> RefreshAsync(SubscriptionSettings settings, IProgress<SubscriptionProgress> progress, CancellationToken token)
    {
        var credentials = new ProviderCredentials("", ""); // Refresh never submits credentials; it only reuses the saved browser session.
        string ingress = secrets.Read("subscription:ingress") ?? throw new InvalidOperationException("请先保存服务器接收凭据。");
        using var uploader = new GrpcSubscriptionUploader(new Uri(settings.ServerEndpoint), ingress);
        var workflow = new SubscriptionWorkflow(new TrackingProvider(provider ?? new TagSubscriptionProvider(), this), new NativeSubscriptionValidator(settings.SurgeCliPath, settings.MihomoPath), uploader, DirectoryPath);
        try
        {
            var result = await workflow.RefreshAsync(settings, credentials, progress, token).ConfigureAwait(false);
            return result;
        }
        catch (LoginRequiredException ex)
        {
            var state = await SaveLoginStateAsync(new(ex.Status, DateTimeOffset.UtcNow, ex.Message), token).ConfigureAwait(false);
            throw new LoginRequiredException(state.Status, state.Detail);
        }
    }
    private sealed class TrackingProvider(ISubscriptionProvider inner, DesktopSubscriptionSession owner) : ISubscriptionProvider
    {
        public async Task<IReadOnlyList<CapturedSubscription>> CaptureAsync(SubscriptionSettings settings, ProviderCredentials credentials, string browserDirectory, IProgress<SubscriptionProgress> progress, CancellationToken token)
        {
            var captures = await inner.CaptureAsync(settings, credentials, browserDirectory, progress, token).ConfigureAwait(false);
            await owner.SaveLoginStateAsync(new(LoginStatus.Valid, DateTimeOffset.UtcNow, "最近一次获取已确认登录有效"), token).ConfigureAwait(false);
            return captures;
        }
    }
    public Task<string> CheckHealthAsync(SubscriptionSettings settings, CancellationToken token) => health?.Invoke(settings, token)
        ?? Task.FromResult("后端尚未提供节点检测结果；未知状态不会触发浏览器刷新。");
}
