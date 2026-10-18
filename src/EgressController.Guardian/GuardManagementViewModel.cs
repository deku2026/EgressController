using System.Collections.ObjectModel;
using EgressController.Core.Protection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Diagnostics;

namespace EgressController.Guardian;

public enum GuardVisualState { Waiting, Healthy, Protecting, Disabled, Unavailable }
public sealed record GuardEventRow(ProtectionEvent Event)
{
    public string Application => Event.Application;
    public string At => Event.At.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Result => Event.Succeeded ? "已终止" : "终止失败";
    public bool Failed => !Event.Succeeded;
    public string Detail => $"{Event.Path}\nPID {Event.Pid}\n{Event.Reason}" + (Event.Error is null ? "" : "\n" + Event.Error);
}

public sealed class GuardManagementViewModel : ObservableObject
{
    private readonly IGuardManagementClient _client;
    private bool _refreshing;
    private bool _busy;
    private long _operationVersion;
    private bool _autoStart = true;
    private string _summary = "正在读取独立守护状态";
    private string _details = "";
    private string _message = "";
    private readonly DiagnosticJournal? _diagnostics;
    private string _lastFailure = "";
    private bool _running, _installed;
    private bool _confirmedAutoStart = true;
    private bool _recordsAvailable;
    private GuardVisualState _visualState = GuardVisualState.Waiting;
    private string _lastChecked = "等待首次检查", _serviceName = "尚未读取", _serviceState = "未知", _targetSummary = "等待同步名单";
    public GuardVisualState VisualState => _visualState;
    public string DisplayReason => Healthy ? "守护待命。仅核验主程序与 TUN，不检查网络连通性。" : Summary;
    public string LastChecked => _lastChecked;
    public string ServiceName => _serviceName;
    public string ServiceState => _serviceState;
    public string TargetSummary => _targetSummary;
    public ObservableCollection<GuardEventRow> EventRows { get; } = [];
    public string StateTitle => VisualState switch
    {
        GuardVisualState.Healthy => "主程序在线，TUN 已接管",
        GuardVisualState.Protecting => "正在保护所选应用",
        GuardVisualState.Disabled => "独立守护已停用",
        GuardVisualState.Unavailable => _serviceState == "未安装" ? "守护服务尚未安装" : _serviceState == "已停止" ? "守护服务未运行" : "无法确认守护状态",
        _ => "正在读取守护状态",
    };
    public GuardManagementViewModel(IGuardManagementClient client, DiagnosticJournal? diagnostics = null,
        string logDirectory = "")
    {
        _client = client;
        _diagnostics = diagnostics;
        LogDirectory = logDirectory;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        InstallCommand = Command(GuardAction.Install);
        StartCommand = Command(GuardAction.Start);
        StopCommand = Command(GuardAction.Stop);
        ForceStopCommand = Command(GuardAction.ForceStop);
        StopAndDisableCommand = Command(GuardAction.StopAndDisable);
        UninstallCommand = Command(GuardAction.Uninstall);
        ClearHistoryCommand = new AsyncRelayCommand(async () => await RunAsync("清空记录", () => _client.ClearHistoryAsync()));
    }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string LastFailure { get => _lastFailure; private set => SetProperty(ref _lastFailure, value); }
    public string LogDirectory { get; }
    public string DiagnosticLogPath => _diagnostics?.LogPath ?? "";
    public string LogFailure => _diagnostics?.WriteFailure ?? "";
    public bool Red => VisualState is not (GuardVisualState.Healthy or GuardVisualState.Disabled);
    public bool Healthy => VisualState == GuardVisualState.Healthy;
    public bool Disabled => VisualState == GuardVisualState.Disabled;
    public bool CanEdit => !_busy;
    public bool CanInstall => !_busy && !_installed;
    public bool CanStart => !_busy && _installed && !_running;
    public bool CanStop => !_busy && _running;
    public bool CanUninstall => !_busy && _installed;
    public bool HasRecords => EventRows.Count > 0;
    public bool NoRecords => !HasRecords;
    public string RecordsEmptyText => _recordsAvailable ? "暂无终止记录" : "服务记录暂不可读";
    public bool CanClearHistory => !_busy && _running && _recordsAvailable;
    public string RecordsHint => !_recordsAvailable && HasRecords ? "上次读取的记录 · 最近 24 小时" : "最近 24 小时 · 最多 200 条";
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasFailure => !string.IsNullOrWhiteSpace(LastFailure) || !string.IsNullOrWhiteSpace(LogFailure);
    public string StateLabel => VisualState switch
    { GuardVisualState.Healthy => "正常", GuardVisualState.Protecting => "保护中", GuardVisualState.Disabled => "已停用", GuardVisualState.Unavailable => "需处理", _ => "检查中" };
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (_busy || !SetProperty(ref _autoStart, value)) return;
            _ = RunAsync(value ? "启用自启" : "关闭自启", () => _client.ExecuteAsync(value ? GuardAction.EnableAutoStart : GuardAction.DisableAutoStart));
        }
    }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand ForceStopCommand { get; }
    public IAsyncRelayCommand StopAndDisableCommand { get; }
    public IAsyncRelayCommand UninstallCommand { get; }
    public IAsyncRelayCommand ClearHistoryCommand { get; }
    private AsyncRelayCommand Command(GuardAction action) => new(() => RunAsync(action switch
    {
        GuardAction.Install => "安装服务", GuardAction.Start => "启动守护", GuardAction.Stop => "停止守护",
        GuardAction.ForceStop => "强制停止", GuardAction.StopAndDisable => "停止并关闭自启", GuardAction.Uninstall => "卸载服务", _ => action.ToString()
    }, () => _client.ExecuteAsync(action)));
    public async Task RefreshAsync()
    {
        if (_refreshing || _busy) return;
        _refreshing = true;
        long version = _operationVersion;
        try
        {
            var state = await _client.ReadAsync();
            if (version != _operationVersion) return;
            _running = state.Service.State == GuardServiceState.Running;
            _installed = state.Service.State != GuardServiceState.Missing;
            if (state.Error is { } error) RecordFailure("读取守护状态", error);
            if (state.Guard is { FailedCount: > 0 } failed) RecordFailure("守护保护", failed.Reason);
            _confirmedAutoStart = state.Service.AutoStart;
            SetProperty(ref _autoStart, _confirmedAutoStart, nameof(AutoStart));
            string serviceState = state.Service.State switch
            {
                GuardServiceState.Running => "运行中", GuardServiceState.Stopped => "已停止",
                GuardServiceState.Missing => "未安装", GuardServiceState.Starting => "正在启动",
                GuardServiceState.Stopping => "正在停止", _ => "状态未知",
            };
            Summary = state.Service.State == GuardServiceState.Running
                ? state.Error ?? state.Guard?.Reason ?? "无法确认守护状态"
                : state.UserStopped ? "独立守护已手动停止/卸载，当前不执行守护保护" : "独立守护：" + serviceState;
            bool disabled = state.Service.State != GuardServiceState.Running && state.UserStopped;
            _visualState = state.Error is not null || state.Service.State == GuardServiceState.Unknown || state.Guard?.FailedCount > 0
                ? GuardVisualState.Unavailable : disabled ? GuardVisualState.Disabled
                : _running && state.Guard is { Protecting: false } ? GuardVisualState.Healthy
                : _running && state.Guard is { Protecting: true } ? GuardVisualState.Protecting
                : state.Service.State is GuardServiceState.Starting or GuardServiceState.Stopping ? GuardVisualState.Waiting : GuardVisualState.Unavailable;
            _serviceName = state.Service.Name; _serviceState = serviceState;
            _recordsAvailable = state.Guard is not null && state.Error is null;
            _targetSummary = state.Guard is { } guard ? (_recordsAvailable ? "" : "上次名单 · ") + $"{guard.TargetCount} 个固定 EXE 路径" : "保护名单暂不可读";
            _lastChecked = "最近检查 " + DateTimeOffset.Now.ToString("HH:mm:ss");
            if (_recordsAvailable && state.Guard is { } current && !EventRows.Select(row => row.Event).SequenceEqual(current.Events))
            {
                EventRows.Clear(); foreach (var item in current.Events) EventRows.Add(new(item));
            }
            Details = $"服务：{state.Service.Name}\n状态：{serviceState} · PID：{state.Service.Pid}\n启动：{(state.Service.AutoStart ? "开机自动" : "手动")}\n账号：{state.Service.Account}\n程序：{state.Service.BinaryPath}"
                + (state.Error is null ? "" : "\n错误：" + state.Error);

        }
        catch (Exception exception)
        {
            if (version != _operationVersion) return;
            _visualState = GuardVisualState.Unavailable;
            _recordsAvailable = false;
            _serviceState = "未知"; _targetSummary = "保护名单暂不可读";
            _lastChecked = "读取失败 " + DateTimeOffset.Now.ToString("HH:mm:ss");
            Details = "服务：" + _serviceName + "\n当前状态未知：" + exception.Message;
            Summary = "读取守护状态失败：" + exception.Message; Message = exception.Message;
            RecordFailure("读取守护状态", exception.Message, exception);
        }
        finally { _refreshing = false; NotifyPresentation(); }
    }
    private async Task RunAsync(string operation, Func<Task> action)
    {
        if (_busy) return;
        _operationVersion++;
        _busy = true; NotifyPresentation();
        try { await action(); Message = operation + "：操作已完成。"; _diagnostics?.Info("守护管理", Message); }
        catch (Exception exception) { SetProperty(ref _autoStart, _confirmedAutoStart, nameof(AutoStart)); Message = operation + "失败：" + exception.Message; RecordFailure(operation, Message, exception); }
        finally { _busy = false; NotifyPresentation(); await RefreshAsync(); }
    }
    public void ReportError(string error) { Message = error; RecordFailure("托盘操作", error); }
    private void RecordFailure(string operation, string message, Exception? error = null)
    {
        LastFailure = $"最近失败 · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} · {operation}\n"
            + DiagnosticJournal.Redact(error?.ToString() ?? message);
        _diagnostics?.Report(operation is "读取守护状态" or "托盘操作" ? "独立守护 · " + operation : "守护管理", "guard.management", message, error);
        OnPropertyChanged(nameof(LogFailure));
        NotifyPresentation();
    }
    private void NotifyPresentation()
    {
        foreach (string name in new[] { nameof(CanEdit), nameof(CanInstall), nameof(CanStart), nameof(CanStop), nameof(CanUninstall),
            nameof(HasRecords), nameof(NoRecords), nameof(RecordsEmptyText), nameof(CanClearHistory), nameof(RecordsHint), nameof(HasMessage), nameof(HasFailure), nameof(StateLabel), nameof(Healthy), nameof(LogFailure), nameof(VisualState), nameof(Red), nameof(Disabled), nameof(StateTitle), nameof(DisplayReason), nameof(LastChecked), nameof(ServiceName), nameof(ServiceState), nameof(TargetSummary) }) OnPropertyChanged(name);
    }
}
