namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private string _workspaceLoadIssue = "";
    private bool _loadedDraft;
    public bool WorkspaceReady => _initialized && !IsLoading && !HasWorkspaceLoadIssue;
    public bool HasWorkspaceLoadIssue => _workspaceLoadIssue.Length > 0;
    public bool WorkspaceBlocked => !WorkspaceReady;
    public bool WorkspaceNavigationEnabled => !OnboardingVisible && ResetClosed;
    public bool CanResetWorkspace => _loadedDraft && !_closing && !IsBusy && !IsLoading;
    public string WorkspaceLoadTitle => HasWorkspaceLoadIssue ? "工作空间未能载入" : "正在载入工作空间";
    public string WorkspaceLoadDescription => HasWorkspaceLoadIssue ? _workspaceLoadIssue : "读取保存的设置、网卡和应用目录…";
    public AsyncUiCommand RetryWorkspaceCommand { get; private set; } = null!;
    private void InitializeWorkspaceCommands() => RetryWorkspaceCommand = new(() => RunOperationAsync(async () =>
    {
        CurrentIssue = "";
        await (_initialization = LoadWorkspaceAsync(ActionToken));
    }), () => HasWorkspaceLoadIssue && !IsBusy && !IsLoading && !_closing);
    private void PublishWorkspaceState()
    {
        foreach (string name in new[] { nameof(WorkspaceReady), nameof(WorkspaceBlocked), nameof(HasWorkspaceLoadIssue), nameof(WorkspaceLoadTitle),
            nameof(WorkspaceLoadDescription), nameof(CanResetWorkspace), nameof(IsLoading), nameof(CanEdit), nameof(StatusTitle), nameof(SaveStatus) }) Raise(name);
        RetryWorkspaceCommand?.Refresh();
    }
}
