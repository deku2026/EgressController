using EgressController.Desktop.Preview;
using EgressController.Desktop.ViewModels;
using EgressController.Policy;

namespace EgressController.Client.Tests;

public sealed class WorkspaceLoadTests
{
    [Fact]
    public async Task DiscoveryFailureKeepsSavedInventoryAndRetryDoesNotSavePlaceholderData()
    {
        PreviewEnvironment.Enter();
        var document = PreviewFixtures.CreateDocument();
        var before = PolicyYaml.Write(document);
        var backend = new FixtureClientBackend { WorkspaceDocument = document, LoadIssue = "Synthetic interface failure" };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        Assert.True(shell.HasWorkspaceLoadIssue); Assert.False(shell.CanEdit); Assert.False(shell.OnboardingVisible);
        Assert.DoesNotContain("已同步", shell.SaveStatus); Assert.Equal(before, PolicyYaml.Write(shell.Document));
        Assert.True(shell.RequestResetCommand.CanExecute(null)); Assert.Equal(0, backend.SaveCalls);
        backend.LoadIssue = null; await shell.RetryWorkspaceCommand.ExecuteAsync();
        Assert.True(shell.WorkspaceReady); Assert.True(shell.CanEdit); Assert.True(shell.CanResetWorkspace);
        Assert.False(shell.HasWorkspaceLoadIssue); Assert.Equal(before, PolicyYaml.Write(shell.Document));
        Assert.Equal(2, backend.LoadCalls); Assert.Equal(0, backend.SaveCalls);
    }

    [Fact]
    public async Task UnreadableDraftNeverBecomesAnEditableEmptyConfiguration()
    {
        PreviewEnvironment.Enter();
        var backend = new FixtureClientBackend { LoadFailure = new IOException("Synthetic storage failure") };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        Assert.True(shell.HasWorkspaceLoadIssue); Assert.False(shell.CanEdit); Assert.False(shell.CanResetWorkspace);
        Assert.False(shell.HasChanges); Assert.Equal(0, backend.SaveCalls);
        backend.LoadFailure = null; await shell.RetryWorkspaceCommand.ExecuteAsync();
        Assert.True(shell.WorkspaceReady); Assert.NotEmpty(shell.Document.Applications); Assert.Equal(0, backend.SaveCalls);
    }

    [Fact]
    public async Task EmptyInterfaceDiscoveryIsActionableAndDoesNotShowEmptySetup()
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend { InterfaceOverride = [] };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        Assert.True(shell.HasWorkspaceLoadIssue); Assert.False(shell.OnboardingVisible);
        Assert.Contains("物理网卡", shell.WorkspaceLoadDescription);
        shell.RequestResetCommand.Execute(null); await shell.ConfirmResetCommand.ExecuteAsync();
        Assert.Empty(shell.Document.Applications); Assert.Empty(shell.Document.Nodes);
        Assert.False(shell.OnboardingVisible); Assert.True(shell.RetryWorkspaceCommand.CanExecute(null));
        Assert.Contains("已重置", shell.WorkspaceLoadDescription);
    }

    [Fact]
    public async Task PendingLoadDoesNotExposeEditableDefaults()
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend { LoadGate = new() };
        await using var shell = new ShellViewModel(backend); var loading = shell.InitializeAsync();
        Assert.True(shell.IsLoading); Assert.False(shell.CanEdit); Assert.False(shell.WorkspaceReady);
        Assert.False(shell.CanResetWorkspace); Assert.False(shell.OnboardingVisible);
        backend.LoadGate.SetResult(); await loading;
        Assert.True(shell.WorkspaceReady); Assert.Equal(0, backend.SaveCalls);
    }

    [Fact]
    public async Task CancelingRetryLeavesSavedChoicesAndCanRetryAgain()
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend { LoadIssue = "Synthetic failure" };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync();
        var saved = PolicyYaml.Write(shell.Document); backend.LoadIssue = null; backend.LoadGate = new();
        Task retry = shell.RetryWorkspaceCommand.ExecuteAsync();
        Assert.True(shell.IsLoading); shell.CancelOperationCommand.Execute(null); await retry;
        Assert.False(shell.IsBusy); Assert.False(shell.IsLoading); Assert.False(shell.WorkspaceReady);
        Assert.Contains("取消", shell.WorkspaceLoadDescription); Assert.True(shell.RetryWorkspaceCommand.CanExecute(null));
        Assert.Equal(saved, PolicyYaml.Write(shell.Document)); Assert.Equal(0, backend.SaveCalls);
        backend.LoadGate.SetResult(); await shell.RetryWorkspaceCommand.ExecuteAsync(); Assert.True(shell.WorkspaceReady);
    }

    [Fact]
    public async Task ModalBlocksBackgroundNavigationAndRestoresCommandsOnClose()
    {
        PreviewEnvironment.Enter(); await using var shell = new ShellViewModel();
        shell.OnboardingVisible = true; Assert.False(shell.AppsCommand.CanExecute(null));
        shell.OnboardingVisible = false; Assert.True(shell.AppsCommand.CanExecute(null));
        shell.RequestResetCommand.Execute(null); Assert.False(shell.NodesCommand.CanExecute(null));
        shell.CancelResetCommand.Execute(null); Assert.True(shell.NodesCommand.CanExecute(null));
    }
}
