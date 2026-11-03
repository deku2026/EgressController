using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Subscriptions;

namespace EgressController.SubscriptionUI;

public sealed class PreviewSubscriptionSession(string scenario = "ready") : ISubscriptionSession
{
    public bool IsPreview => true;
    public Task<LoginState> ReadLoginStateAsync(CancellationToken token) => Task.FromResult(new LoginState(scenario switch { "expired" => LoginStatus.Expired, "verification" => LoginStatus.NeedsVerification, "unknown" => LoginStatus.Unknown, "empty" => LoginStatus.NotSignedIn, _ => LoginStatus.Valid }, DateTimeOffset.UtcNow, scenario switch { "expired" => "网站已返回登录页，保存的会话已失效；请重新登录", "verification" => "网站安全验证阻塞；自动刷新已暂停，请手动完成验证", "unknown" => "网络暂不可用，无法确认登录状态；未判定为过期", "empty" => "首次使用请打开专属 Chrome 完成登录", _ => "登录有效 · 复用已保存的专属 Chrome 会话" }));
    public Task<LoginState> LoginAsync(SubscriptionSettings settings, IProgress<SubscriptionProgress> progress, CancellationToken token) => ReadLoginStateAsync(token);
    public Task<LoginState> CheckSessionAsync(SubscriptionSettings settings, CancellationToken token) => ReadLoginStateAsync(token);
    public Task<SubscriptionSettings> LoadAsync(CancellationToken token) => Task.FromResult(new SubscriptionSettings());
    public Task SaveAsync(SubscriptionSettings settings, string username, string password, string ingressToken, CancellationToken token) => Task.CompletedTask;
    public Task<SubscriptionResult[]> RefreshAsync(SubscriptionSettings settings, IProgress<SubscriptionProgress> progress, CancellationToken token)
    {
        progress.Report(new("端到端上传成功 · 模拟", "Clash / Surge 均已通过校验，服务器已确认"));
        return Task.FromResult(new[] { new SubscriptionResult(SourceFormat.Clash, "demo", "demo", 1), new SubscriptionResult(SourceFormat.Surge, "demo", "demo", 1) });
    }
    public Task<string> CheckHealthAsync(SubscriptionSettings settings, CancellationToken token) => Task.FromResult("HK：12 / 12 已完成 · 11 成功 · 1 超时 · 未达到刷新条件");
}
public sealed partial class SubscriptionViewModel(ISubscriptionSession session) : ObservableObject, IDisposable
{
    private SubscriptionSettings _settings = new();
    private CancellationTokenSource? _operation;
    private readonly CancellationTokenSource _lifetime = new();
    [ObservableProperty] private bool _loginWarning;
    [ObservableProperty] private string _surgeValidationLabel = OperatingSystem.IsMacOS() ? "Surge CLI 配置检查" : "Surge 结构检查 · 原生校验需 macOS";
    [ObservableProperty] private string _loginStatusText = "尚未确认登录状态";
    [ObservableProperty] private string _loginDetail = "首次使用请手动登录，完成验证码后保存会话";
    [ObservableProperty] private bool _loginRequired = true;
    [ObservableProperty] private string _website = "https://tagss.pro/";
    [ObservableProperty] private string _ingressToken = "";
    [ObservableProperty] private string _serverEndpoint = "http://127.0.0.1:18081/";
    [ObservableProperty] private string _chromePath = "";
    [ObservableProperty] private string _mihomoPath = "";
    [ObservableProperty] private string _surgeCliPath = "";
    [ObservableProperty] private bool _automaticChecks;
    [ObservableProperty] private bool _keepSessionActive = true;
    [ObservableProperty] private string _groupPattern = "(?i)(香港|HK|Hong Kong)";
    [ObservableProperty] private int _failurePercent = 50;
    [ObservableProperty] private int _consecutiveRounds = 2;
    [ObservableProperty] private int _cooldownMinutes = 60;
    [ObservableProperty] private string _stage = "等待首次获取";
    [ObservableProperty] private string _detail = "首次登录后，按需复用专属 Chrome 会话";
    [ObservableProperty] private string _clashStatus = "尚未获取";
    [ObservableProperty] private string _surgeStatus = "尚未获取";
    [ObservableProperty] private string _uploadStatus = "尚未上传";
    [ObservableProperty] private string _healthStatus = "尚未检测 · 未知结果不计为失败";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _needsInteraction;
    public bool IsPreview => session.IsPreview;
    public async Task InitializeAsync()
    {
        try
        {
            _settings = await session.LoadAsync(default); Website = _settings.Website; ServerEndpoint = _settings.ServerEndpoint;
            ChromePath = _settings.ChromePath; MihomoPath = _settings.MihomoPath; SurgeCliPath = _settings.SurgeCliPath;
            AutomaticChecks = _settings.AutomaticChecks; KeepSessionActive = _settings.KeepSessionActive; GroupPattern = _settings.GroupPattern; FailurePercent = _settings.FailurePercent;
            ConsecutiveRounds = _settings.ConsecutiveRounds; CooldownMinutes = _settings.CooldownMinutes;
            ShowLogin(await session.ReadLoginStateAsync(_lifetime.Token), current: session.IsPreview);
            if (!session.IsPreview) _ = ObserveSavedStateAsync();
        }
        catch (Exception ex) { Stage = "设置读取失败"; Detail = ex.Message; }
    }
    private SubscriptionSettings Snapshot() => _settings with { Website = Website, ServerEndpoint = ServerEndpoint, ChromePath = ChromePath,
        MihomoPath = MihomoPath, SurgeCliPath = SurgeCliPath, AutomaticChecks = AutomaticChecks, KeepSessionActive = KeepSessionActive, GroupPattern = GroupPattern,
        FailurePercent = FailurePercent, ConsecutiveRounds = ConsecutiveRounds, CooldownMinutes = CooldownMinutes };
    [RelayCommand] private Task SaveAsync() => RunAsync(async token =>
    {
        var settings = Snapshot();
        await session.SaveAsync(settings, "", "", IngressToken, token); _settings = settings;
        IngressToken = ""; Stage = "设置已保存"; Detail = "接收凭据已存入系统安全存储；留空表示保留现有凭据";
    });
    [RelayCommand] private Task LoginAsync() => RunAsync(async token =>
    {
        Stage = "等待手动登录"; NeedsInteraction = true;
        var result = await session.LoginAsync(Snapshot(), new Progress<SubscriptionProgress>(p => { Stage = p.Stage; Detail = p.Detail; NeedsInteraction = p.NeedsInteraction; }), token);
        ShowLogin(result, true); NeedsInteraction = result.Status == LoginStatus.NeedsVerification;
        Stage = LoginStatusText; Detail = LoginDetail;
    });
    [RelayCommand] private Task CheckSessionAsync() => RunAsync(async token => ShowLogin(await session.CheckSessionAsync(Snapshot(), token), true));
    private async Task ObserveSavedStateAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                if (Busy) continue;
                try
                {
                    var state = await session.ReadLoginStateAsync(_lifetime.Token);
                    ShowLogin(state, DateTimeOffset.UtcNow - state.CheckedAt < TimeSpan.FromMinutes(5));
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { LoginStatusText = "会话状态无法读取"; LoginDetail = "状态文件不可用；请手动检查会话"; LoginWarning = true; }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private void ShowLogin(LoginState state, bool current)
    {
        LoginRequired = state.Status is LoginStatus.Expired or LoginStatus.NeedsVerification or LoginStatus.NotSignedIn;
        LoginWarning = state.Status is LoginStatus.Expired or LoginStatus.NeedsVerification;
        LoginStatusText = state.Status switch
        {
            LoginStatus.Valid when current => "登录有效",
            LoginStatus.Valid => "已保存会话 · 待确认",
            LoginStatus.Expired => "会话已过期 · 需要重新登录",
            LoginStatus.NeedsVerification => "网站验证阻塞 · 需要人工处理",
            LoginStatus.NotSignedIn => "尚未登录",
            _ => "尚未确认登录状态"
        };
        LoginDetail = state.Detail + (state.CheckedAt == default ? "" : " · " + state.CheckedAt.ToLocalTime().ToString("MM-dd HH:mm"));
    }
    [RelayCommand] private Task RefreshAsync() => RunAsync(async token =>
    {
        Stage = "准备刷新"; Detail = "正在复用已保存的专属 Chrome 会话"; UploadStatus = "等待本次获取与确认"; NeedsInteraction = false;
        var results = await session.RefreshAsync(Snapshot(), new Progress<SubscriptionProgress>(progress =>
        { Stage = progress.Stage; Detail = progress.Detail; NeedsInteraction = progress.NeedsInteraction; }), token);
        ClashStatus = results.Any(r => r.Format == SourceFormat.Clash) ? "原生校验通过" : "本次未提供";
        SurgeStatus = results.Any(r => r.Format == SourceFormat.Surge) ? (OperatingSystem.IsMacOS() ? "原生校验通过" : "结构校验通过") : "本次未提供";
        UploadStatus = session.IsPreview ? "哈希与版本已确认 · 模拟" : "哈希与版本已确认";
        Stage = session.IsPreview ? "端到端上传成功 · 模拟" : "端到端上传成功"; NeedsInteraction = false; ShowLogin(await session.ReadLoginStateAsync(token), true);
    });
    [RelayCommand] private Task CheckHealthAsync() => RunAsync(async token => HealthStatus = await session.CheckHealthAsync(Snapshot(), token));
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        var owned = new CancellationTokenSource(); _operation = owned; Busy = true;
        try { await action(owned.Token); }
        catch (OperationCanceledException) when (owned.IsCancellationRequested) { Stage = "已取消"; Detail = "已保留有效文件，未改变当前网络配置"; }
        catch (LoginRequiredException ex) { ShowLogin(new(ex.Status, DateTimeOffset.UtcNow, ex.Message), true); Stage = LoginStatusText; Detail = LoginDetail; NeedsInteraction = true; }
        catch (Exception ex) { Stage = "操作未完成"; Detail = ex.Message; }
        finally { if (ReferenceEquals(_operation, owned)) { _operation = null; Busy = false; } owned.Dispose(); }
    }
    public void Dispose() { _operation?.Cancel(); _lifetime.Cancel(); }
}
