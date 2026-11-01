using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ConfigurationResetTests
{
    [Theory]
    [InlineData("stop")]
    [InlineData("storage")]
    [InlineData("cancel")]
    [InlineData("")]
    public async Task ResetRequiresRestorationBeforeStorageAndNeverMutatesTheCaller(string failure)
    {
        var document = ConfigurationTests.Policy(); string original = PolicyYaml.Write(document);
        var calls = new List<string>(); using var cancel = new CancellationTokenSource();
        Task Reset() => ConfigurationReset.ExecuteAsync(document, _ =>
        {
            calls.Add("stop"); if (failure == "cancel") cancel.Cancel(); return Task.FromResult(failure != "stop");
        }, (snapshot, _) =>
        {
            calls.Add("backup-reset"); Assert.Equal(original, PolicyYaml.Write(snapshot));
            if (failure == "storage") throw new IOException("Synthetic write failure");
            return Task.CompletedTask;
        }, cancel.Token);
        if (failure.Length == 0) await Reset();
        else await Assert.ThrowsAnyAsync<Exception>(Reset);
        Assert.Equal(failure is "stop" or "cancel" ? new[] { "stop" } : new[] { "stop", "backup-reset" }, calls);
        Assert.Equal(original, PolicyYaml.Write(document));
    }
    [Fact]
    public async Task ResetPersistsEmptySetupAndRetainsPrivateBackupAcrossFurtherSavesAndCorruption()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-reset-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DraftPolicyStore(root); var original = ConfigurationTests.Policy();
            await store.SaveAsync(original, TestContext.Current.CancellationToken);
            original.Interfaces.HomeId = "edited-unsaved-choice";
            await store.ResetAsync(original, TestContext.Current.CancellationToken);
            string backup = Path.Combine(root, "policy.before-reset.draft"); byte[] saved = File.ReadAllBytes(backup);
            Assert.Contains("edited-unsaved-choice", File.ReadAllText(backup));
            Assert.Equal(PolicyYaml.Write(new()), PolicyYaml.Write((await store.LoadAsync(TestContext.Current.CancellationToken))!));
            await store.SaveAsync(new(), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "policy.draft"), "damaged", TestContext.Current.CancellationToken);
            Assert.Equal(PolicyYaml.Write(new()), PolicyYaml.Write((await store.LoadAsync(TestContext.Current.CancellationToken))!));
            Assert.Equal(saved, File.ReadAllBytes(backup));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
