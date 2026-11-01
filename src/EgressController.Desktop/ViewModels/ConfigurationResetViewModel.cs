using EgressController.Desktop.Services;
using EgressController.Policy;

namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _resetRequested;
    private string _resetIssue = "";
    public bool ResetRequested { get => _resetRequested; private set { if (Set(ref _resetRequested, value)) { Raise(nameof(ResetClosed)); ConfirmResetCommand?.Refresh(); } } }
    public bool ResetClosed => !ResetRequested;
    public string ResetIssue { get => _resetIssue; private set { if (Set(ref _resetIssue, value)) Raise(nameof(HasResetIssue)); } }
    public bool HasResetIssue => ResetIssue.Length > 0;
    public UiCommand RequestResetCommand { get; private set; } = null!;
    public UiCommand CancelResetCommand { get; private set; } = null!;
    public AsyncUiCommand ConfirmResetCommand { get; private set; } = null!;
    private void InitializeResetCommands()
    {
        RequestResetCommand = new(() => { ResetIssue = ""; ResetRequested = true; }, () => CanEdit);
        CancelResetCommand = new(() => ResetRequested = false, () => !IsBusy);
        ConfirmResetCommand = new(() => RunOperationAsync(ResetConfigurationAsync), () => ResetRequested && CanEdit);
    }
    private async Task ResetConfigurationAsync()
    {
        ResetIssue = "";
        try { await _backend.ResetConfigurationAsync(Document, ActionToken); }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            RecordFailure("configuration.reset", error);
            ResetIssue = SafeFailure(error, "重置未完成，原配置仍保留。请打开日志目录检查后重试。");
            // Stop may have succeeded before a storage failure. Re-read rather than retaining a healthy badge.
            await RefreshStatusAsync();
            return;
        }
        ++_revision; ++_attemptRevision;
        _backend.SetProgressSink(null); _appliedGroups = null; _lastGroupTests.Clear(); _probes.Clear();
        _prepared = new([], new Dictionary<string, byte[]>(), new Dictionary<string, GeoRuleSet>());
        _ruleCatalog = []; _catalog.Clear(); // Discard checkboxes without rescanning/enrolling any executable.
        Document = new(); _subscriptionReady = false; _needsValidation = true; ConfigValid = false; HasChanges = false;
        SubscriptionUrl = ""; LocalSubscriptionYaml = ""; AppQuery = ""; CatalogQuery = ""; RuleQuery = ""; NodeQuery = "";
        CustomRuleName = ""; CustomRuleEntries = ""; CustomRuleIsIp = false;
        AddingApps = false; AddingRules = false; CreatingCustomRule = false; ManagingGroups = false;
        RebuildProjection(); GeneratedConfig = "完成设置并校验后生成运行配置。";
        ConnectionAttempt.Reset(); CurrentIssue = ""; _statusIssue = null;
        _state = new(false, true, _state.HelperInstalled, "已重置配置，等待重新设置。已验证的资源缓存与日志继续保留。");
        PreviewRunning = false; SetRuntimeUnavailable(false);
        if (Connections.IsPaused) Connections.TogglePauseCommand.Execute(null);
        Connections.Reconcile(new([], _backend.Now, true, "完成设置并连接后开始观测。"));
        Connections.ResetFiltersCommand.Execute(null); Connections.IsOpen = false;
        PageIndex = 0; SetupStep = 0; OnboardingVisible = true; ResetRequested = false;
        foreach (string property in new[] { nameof(ConfigValid), nameof(ValidationLabel), nameof(SetupSubscriptionStatus), nameof(SourceStatusText), nameof(ProxyResolvedName), nameof(TunStatusText) }) Raise(property);
        Notice = "已备份原配置并重置。从选择两张网卡开始重新设置。";
        AddEvent("重置并重新设置", "已确认停止接管，备份原配置并保存空白设置；缓存与历史日志保留。", "lifecycle");
    }
}
