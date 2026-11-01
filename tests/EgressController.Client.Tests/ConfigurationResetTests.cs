using EgressController.Desktop.Preview;
using EgressController.Desktop.ViewModels;
using EgressController.Desktop.Services;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Client.Tests;

public sealed class ConfigurationResetTests
{
    [Fact]
    public async Task UnavailableHelperCannotVetoResetAndRepairDoesNotStartCoreOrOverwriteNewSettings()
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend(); await using var shell = new ShellViewModel(backend);
        await shell.InitializeAsync(); await shell.ApplyPreviewAsync();
        int starts = backend.Core.RestartCount; string original = PolicyYaml.Write(shell.Document);
        backend.SimulateHelperUnavailable = true; await shell.RefreshStatusAsync();
        shell.RequestResetCommand.Execute(null); await shell.ConfirmResetCommand.ExecuteAsync();
        Assert.False(shell.ResetRequested); Assert.False(shell.HasResetIssue); Assert.False(shell.HasIssue);
        Assert.True(shell.OnboardingVisible); Assert.Equal(0, shell.SetupStep);
        Assert.Equal(PolicyYaml.Write(new()), PolicyYaml.Write(shell.Document));
        Assert.Equal(original, File.ReadAllText(backend.ResetBackupPath!));
        Assert.Equal(PolicyYaml.Write(new()), File.ReadAllText(backend.SavedPath!));
        Assert.True(shell.HasRecoveryIssue); Assert.False(shell.PreviewRunning);
        Assert.Equal("后台状态待修复", shell.TunStatusText);
        Assert.Equal(0, backend.RepairCount); Assert.Equal(starts, backend.Core.RestartCount);
        shell.SelectedHomeInterface = shell.InterfaceChoices[0]; shell.SelectedNonHomeInterface = shell.InterfaceChoices[1];
        await shell.CheckHomeCommand.ExecuteAsync();
        await shell.NextSetupCommand.ExecuteAsync(); Assert.Equal(1, shell.SetupStep);
        await shell.RefreshStatusAsync(); Assert.True(shell.HasRecoveryIssue);
        string restarted = PolicyYaml.Write(shell.Document);
        backend.SimulateRepairFailure = true; await shell.RepairNetworkCommand.ExecuteAsync();
        Assert.True(shell.HasRecoveryIssue); Assert.True(shell.RepairNetworkCommand.CanExecute(null));
        Assert.Equal(restarted, PolicyYaml.Write(shell.Document));
        backend.SimulateRepairFailure = false; await shell.RepairNetworkCommand.ExecuteAsync();
        Assert.False(shell.HasRecoveryIssue); Assert.False(shell.PreviewRunning);
        Assert.Equal(2, backend.RepairCount); Assert.Equal(starts, backend.Core.RestartCount);
        Assert.Equal(restarted, PolicyYaml.Write(shell.Document)); Assert.Equal(1, shell.SetupStep);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResetPreservesPolicyAndRefreshesActualStoppedState(bool storageFailure)
    {
        PreviewEnvironment.Enter(); var backend = new ResetFailureBackend(storageFailure); await using var shell = new ShellViewModel(backend);
        await shell.InitializeAsync(); string original = PolicyYaml.Write(shell.Document);
        shell.RequestResetCommand.Execute(null); await shell.ConfirmResetCommand.ExecuteAsync();
        Assert.True(shell.ResetRequested); Assert.True(shell.HasResetIssue); Assert.False(shell.IsBusy);
        Assert.Equal(original, PolicyYaml.Write(shell.Document)); Assert.Equal(!storageFailure, shell.PreviewRunning);
        Assert.DoesNotContain("private", shell.ResetIssue); Assert.True(shell.CancelResetCommand.CanExecute(null));
    }
    [Fact]
    public async Task CancelPreservesEverythingAndConfirmedResetReturnsToEmptyFirstStepWithoutStartingCore()
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend(); await using var shell = new ShellViewModel(backend);
        await shell.InitializeAsync(); await shell.ApplyPreviewAsync();
        int starts = backend.Core.RestartCount; string before = PolicyYaml.Write(shell.Document);
        shell.RequestResetCommand.Execute(null);
        Assert.True(shell.ResetRequested); Assert.Null(backend.ResetBackupPath);
        shell.CancelResetCommand.Execute(null);
        Assert.False(shell.ResetRequested); Assert.Equal(before, PolicyYaml.Write(shell.Document)); Assert.True(shell.PreviewRunning);
        shell.ConnectionAttempt.Fail("Synthetic route update failure"); shell.SubscriptionUrl = "https://secret.example.invalid/sub";
        shell.LocalSubscriptionYaml = "synthetic pending text"; shell.CatalogQuery = "Safari";
        shell.RequestResetCommand.Execute(null); await shell.ConfirmResetCommand.ExecuteAsync();
        Assert.False(shell.ResetRequested); Assert.False(shell.PreviewRunning); Assert.False(shell.ConnectionAttempt.IsVisible);
        Assert.Equal(starts, backend.Core.RestartCount); Assert.Equal(before, File.ReadAllText(backend.ResetBackupPath!));
        Assert.Equal(PolicyYaml.Write(new()), PolicyYaml.Write(shell.Document)); Assert.Empty(shell.AllGroups); Assert.Empty(shell.VisibleApps);
        Assert.Empty(shell.SubscriptionUrl); Assert.Empty(shell.LocalSubscriptionYaml); Assert.Empty(shell.CatalogQuery);
        Assert.Null(shell.SelectedHomeInterface); Assert.Null(shell.SelectedNonHomeInterface); Assert.Null(shell.SelectedGlobalTarget);
        Assert.True(shell.OnboardingVisible); Assert.Equal(0, shell.SetupStep); Assert.False(shell.NextSetupCommand.CanExecute(null));
        Assert.False(shell.HasIssue); Assert.False(shell.HasChanges); Assert.False(shell.ConfigValid);
        Assert.Equal(PolicyYaml.Write(new()), File.ReadAllText(backend.SavedPath!));
        shell.SelectedHomeInterface = shell.InterfaceChoices[0]; shell.SelectedNonHomeInterface = shell.InterfaceChoices[1];
        await shell.CheckHomeCommand.ExecuteAsync();
        await shell.NextSetupCommand.ExecuteAsync(); Assert.Equal(1, shell.SetupStep);
        shell.SubscriptionUrl = PreviewFixtures.SubscriptionUrl; await shell.ImportSubscriptionCommand.ExecuteAsync();
        Assert.NotEmpty(shell.Document.Groups); Assert.Equal(starts, backend.Core.RestartCount);
    }
    private sealed class ResetFailureBackend(bool storageFailure) : IClientBackend
    {
        private readonly FixtureClientBackend _inner = new();
        public bool IsPreview => true;
        public DateTimeOffset Now => _inner.Now;
        public string LogDirectory => _inner.LogDirectory;
        public Task<ClientBootstrap> LoadAsync(CancellationToken token) => _inner.LoadAsync(token);
        public Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken token) => _inner.DiscoverApplicationsAsync(token);
        public Task<ClientApplication> BuildInventoryAsync(ClientApplication app, IReadOnlyList<string> paths, CancellationToken token) => _inner.BuildInventoryAsync(app, paths, token);
        public Task<byte[]?> LoadIconAsync(string path, CancellationToken token) => _inner.LoadIconAsync(path, token);
        public Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument doc, CancellationToken token) => _inner.LoadRuleCatalogAsync(doc, token);
        public Task<MacPolicyDocument> ImportSubscriptionAsync(Uri uri, MacPolicyDocument doc, CancellationToken token) => _inner.ImportSubscriptionAsync(uri, doc, token);
        public Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument doc, CancellationToken token) => _inner.PrepareResourcesAsync(doc, token);
        public Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument doc, InterfaceBinding binding, CancellationToken token) => _inner.ProbeAsync(doc, binding, token);
        public Task<ApplyResult> ApplyAsync(MacPolicyDocument doc, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken token) => _inner.ApplyAsync(doc, options, assets, token);
        public Task<bool> StopAsync(CancellationToken token) => _inner.StopAsync(token);
        public Task<ClientRuntimeState> ReadStatusAsync(CancellationToken token) => _inner.ReadStatusAsync(token);
        public Task SaveDraftAsync(MacPolicyDocument doc, CancellationToken token) => _inner.SaveDraftAsync(doc, token);
        public Task OpenLogDirectoryAsync(CancellationToken token) => _inner.OpenLogDirectoryAsync(token);
        public Task<ConfigurationResetResult> ResetConfigurationAsync(MacPolicyDocument doc, CancellationToken token) => ConfigurationReset.ExecuteAsync(doc,
            cancellation => storageFailure ? _inner.StopAsync(cancellation) : Task.FromResult(false),
            (_, _) => throw new IOException("private fixture details"), token);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
