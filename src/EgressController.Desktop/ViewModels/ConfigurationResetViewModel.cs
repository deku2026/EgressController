using EgressController.Desktop.Services;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _resetRequested;
    private string _resetIssue = "";
    private bool _resetRecoveryPending;
    private string _recoveryFailure = "";
    public bool HasRecoveryIssue => _resetRecoveryPending || _runtimeUnavailable;
    public string RecoveryMessage => _recoveryFailure.Length > 0 ? _recoveryFailure : _resetRecoveryPending
        ? "设置已重置，可以从头配置。系统网络恢复尚未确认，请修复助手后重试恢复。"
        : "无法确认后台助手状态。更新应用后可能需要重新授权，修复不会启动连接。";
    public AsyncUiCommand RepairNetworkCommand { get; private set; } = null!;
    public bool ResetRequested { get => _resetRequested; private set { if (Set(ref _resetRequested, value)) { Raise(nameof(ResetClosed)); Raise(nameof(WorkspaceNavigationEnabled)); RefreshNavigationCommands(); ConfirmResetCommand?.Refresh(); } } }
    public bool ResetClosed => !ResetRequested;
    public string ResetIssue { get => _resetIssue; private set { if (Set(ref _resetIssue, value)) Raise(nameof(HasResetIssue)); } }
    public bool HasResetIssue => ResetIssue.Length > 0;
    public UiCommand RequestResetCommand { get; private set; } = null!;
    public UiCommand CancelResetCommand { get; private set; } = null!;
    public AsyncUiCommand ConfirmResetCommand { get; private set; } = null!;
    private void InitializeResetCommands()
    {
        RequestResetCommand = new(() => { ResetIssue = ""; ResetRequested = true; }, () => CanResetWorkspace);
        CancelResetCommand = new(() => ResetRequested = false, () => !IsBusy);
        ConfirmResetCommand = new(() => RunOperationAsync(ResetConfigurationAsync), () => ResetRequested && CanResetWorkspace);
        RepairNetworkCommand = new(() => RunOperationAsync(RepairNetworkAsync), () => HasRecoveryIssue && CanResetWorkspace);
    }
    private async Task RepairNetworkAsync()
    {
        _recoveryFailure = ""; Raise(nameof(RecoveryMessage));
        Notice = "正在检查助手并恢复网络；如需更新授权，macOS 会提示管理员验证…";
        try
        {
            if (!await _backend.RepairNetworkAsync(ActionToken))
            { _recoveryFailure = "网络恢复尚未确认。可以继续设置，或再次修复；当前设置与后台恢复记录保留。"; return; }
            _resetRecoveryPending = false;
            Notice = "已确认停止接管并恢复网络。可以继续设置，连接由你启动。";
            AddEvent("网络已恢复", "助手已验证，停止与网络恢复已确认；未启动连接。", "lifecycle");
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            RecordFailure("network.repair", error);
            _recoveryFailure = error is OperationCanceledException ? "已取消修复，可以继续设置或稍后重试。"
                : SafeFailure(error, "修复未完成。请打开日志目录查看原因，或再次修复；当前设置继续保留。");
        }
        finally { await RefreshStatusAsync(); Raise(nameof(RecoveryMessage)); }
    }
    private async Task ResetConfigurationAsync()
    {
        ResetIssue = "";
        _draftDelay?.Cancel();
        await _draftWriter.WaitAsync(ActionToken);
        ConfigurationResetResult result;
        try { result = await _backend.ResetConfigurationAsync(Document, ActionToken); }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            RecordFailure("configuration.reset", error);
            ResetIssue = SafeFailure(error, "重置未完成，原配置仍保留。请打开日志目录检查后重试。");
            // Stop may have succeeded before a storage failure. Re-read rather than retaining a healthy badge.
            await RefreshStatusAsync();
            return;
        }
        finally { _draftWriter.Release(); }
        ++_revision; _savedDraftRevision = _revision; ++_attemptRevision;
        _backend.SetProgressSink(null); _appliedGroups = null; _lastGroupTests.Clear(); _probes.Clear();
        _prepared = new([], new Dictionary<string, byte[]>(), new Dictionary<string, GeoRuleSet>());
        _ruleCatalog = []; _catalog.Clear(); // Discard checkboxes without rescanning/enrolling any executable.
        Document = new(); SubscriptionDnsIssue = false; ShowSubscriptionDnsRetry = false; InvalidateHomeCheck(true); _subscriptionReady = false; _needsValidation = true; ConfigValid = false; HasChanges = false;
        SubscriptionUrl = ""; LocalSubscriptionYaml = ""; AppQuery = ""; CatalogQuery = ""; RuleQuery = ""; NodeQuery = "";
        CustomRuleName = ""; CustomRuleEntries = ""; CustomRuleIsIp = false;
        AddingApps = false; AddingRules = false; CreatingCustomRule = false; ManagingGroups = false;
        RebuildProjection(); GeneratedConfig = "完成设置并校验后生成运行配置。";
        ConnectionAttempt.Reset(); CurrentIssue = ""; _statusIssue = null;
        _resetRecoveryPending = !result.NetworkRestored;
        _recoveryFailure = "";
        _state = new(false, result.NetworkRestored, _state.HelperInstalled,
            result.NetworkRestored ? "已重置配置，等待重新设置。旧订阅缓存与生成配置已清理，日志和独立备份保留。" : RecoveryMessage);
        PreviewRunning = false; SetRuntimeUnavailable(!result.NetworkRestored);
        if (Connections.IsPaused) Connections.TogglePauseCommand.Execute(null);
        Connections.Reconcile(new([], _backend.Now, true, "完成设置并连接后开始观测。"));
        Connections.ResetFiltersCommand.Execute(null); Connections.IsOpen = false;
        PageIndex = 0; SetupStep = 0; OnboardingVisible = !HasWorkspaceLoadIssue && _interfaces.Count > 0; ResetRequested = false;
        if (HasWorkspaceLoadIssue || _interfaces.Count == 0) _workspaceLoadIssue = "设置已重置。重新载入网卡与应用目录后，即可从第一步开始。";
        PublishWorkspaceState();
        foreach (string property in new[] { nameof(ConfigValid), nameof(ValidationLabel), nameof(SetupSubscriptionStatus), nameof(SourceStatusText), nameof(ProxyResolvedName), nameof(TunStatusText) }) Raise(property);
        Notice = "已备份原配置并重置。从选择两张网卡开始重新设置。";
        AddEvent("重置并重新设置", result.NetworkRestored
            ? "已确认停止接管，备份原配置并保存空白设置；旧缓存和生成配置清理，历史日志保留。"
            : "已备份原配置并保存空白设置；网络恢复尚未确认，后台恢复记录保留，可单独修复。", "lifecycle");
    }
}
