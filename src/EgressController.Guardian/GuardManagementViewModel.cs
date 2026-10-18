using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Diagnostics;

namespace EgressController.Guardian;

public sealed class GuardManagementViewModel : ObservableObject
{
    private readonly IGuardManagementClient _client;
    private bool _refreshing;
    private bool _busy;
    private long _operationVersion;
    private bool _autoStart = true;
    private string _summary = "正在读取独立守护状态";
    private string _details = "";
    private string _records = "";
    private string _message = "";
    private bool _red = true;
    private bool _disabled;
    private readonly DiagnosticJournal? _diagnostics;
    private string _lastFailure = "";
    private bool _running, _installed;
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
    public string Records { get => _records; private set => SetProperty(ref _records, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string LastFailure { get => _lastFailure; private set => SetProperty(ref _lastFailure, value); }
    public string LogDirectory { get; }
    public string DiagnosticLogPath => _diagnostics?.LogPath ?? "";
    public string LogFailure => _diagnostics?.WriteFailure ?? "";
    public bool Red { get => _red; private set => SetProperty(ref _red, value); }
    public bool Healthy => !Red && !Disabled;
    public bool Disabled { get => _disabled; private set => SetProperty(ref _disabled, value); }
    public bool CanEdit => !_busy;
    public bool CanInstall => !_busy && !_installed;
    public bool CanStart => !_busy && _installed && !_running;
    public bool CanStop => !_busy && _running;
    public bool CanUninstall => !_busy && _installed;
    public bool HasRecords => !string.IsNullOrWhiteSpace(Records);
    public bool NoRecords => !HasRecords;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasFailure => !string.IsNullOrWhiteSpace(LastFailure) || !string.IsNullOrWhiteSpace(LogFailure);
    public string StateLabel => Disabled ? "已停用" : Red ? "保护中" : "运行正常";
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!SetProperty(ref _autoStart, value)) return;
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
    private AsyncRelayCommand Command(GuardAction action) => new(() => RunAsync(action.ToString(), () => _client.ExecuteAsync(action)));
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
            SetProperty(ref _autoStart, state.Service.AutoStart, nameof(AutoStart));
            string serviceState = state.Service.State switch
            {
                GuardServiceState.Running => "运行中", GuardServiceState.Stopped => "已停止",
                GuardServiceState.Missing => "未安装", GuardServiceState.Starting => "正在启动",
                GuardServiceState.Stopping => "正在停止", _ => "状态未知",
            };
            Summary = state.Service.State == GuardServiceState.Running
                ? state.Guard?.Reason ?? state.Error ?? "无法确认守护状态"
                : state.UserStopped ? "独立守护已手动停止/卸载，当前不执行守护保护" : "独立守护：" + serviceState;
            Red = state.Service.State == GuardServiceState.Running
                ? state.Guard?.Protecting != false || state.Error is not null : !state.UserStopped;
            Disabled = state.Service.State != GuardServiceState.Running && state.UserStopped;
            Details = $"服务：{state.Service.Name}\n状态：{serviceState} · PID：{state.Service.Pid}\n启动：{(state.Service.AutoStart ? "开机自动" : "手动")}\n账号：{state.Service.Account}\n程序：{state.Service.BinaryPath}"
                + (state.Error is null ? "" : "\n错误：" + state.Error);
            Records = state.Guard is null ? "服务未连接，无法读取记录。" : string.Join("\n\n", state.Guard.Events.Select(item =>
                $"{item.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 独立守护 · {item.Application} · PID {item.Pid}\n{item.Path}\n{item.Reason}\n{(item.Succeeded ? "已终止" : "失败：" + item.Error)}"));
        }
        catch (Exception exception)
        {
            if (version != _operationVersion) return;
            Summary = "无法读取独立守护状态"; Message = exception.Message; Red = true; Disabled = false;
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
        catch (Exception exception) { Message = operation + "失败：" + exception.Message; RecordFailure(operation, Message, exception); }
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
            nameof(HasRecords), nameof(NoRecords), nameof(HasMessage), nameof(HasFailure), nameof(StateLabel), nameof(Healthy), nameof(LogFailure) }) OnPropertyChanged(name);
    }
}
