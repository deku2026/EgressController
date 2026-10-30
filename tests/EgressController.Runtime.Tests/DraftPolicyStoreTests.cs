using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class DraftPolicyStoreTests
{
    [Fact]
    public async Task IncompleteOnboardingPersistsPrivatelyAndCorruptionUsesPriorDraft()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-draft-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DraftPolicyStore(root);
            var draft = new MacPolicyDocument();
            await store.SaveAsync(draft, TestContext.Current.CancellationToken);
            Assert.NotNull(await store.LoadAsync(TestContext.Current.CancellationToken));
            draft.Interfaces.HomeId = "saved-choice";
            await store.SaveAsync(draft, TestContext.Current.CancellationToken);
            Assert.Equal("saved-choice", (await store.LoadAsync(TestContext.Current.CancellationToken))!.Interfaces.HomeId);
            await File.WriteAllTextAsync(Path.Combine(root, "policy.draft"), "corrupt", TestContext.Current.CancellationToken);
            Assert.Equal("", (await store.LoadAsync(TestContext.Current.CancellationToken))!.Interfaces.HomeId);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(root, "policy.previous.draft")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void LifecycleFileRemainsBoundedAndDoesNotAcceptLineInjection()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new PrivateLifecycleFileLog(root, 1024);
            for (int i = 0; i < 100; i++) log.Append(new(DateTimeOffset.UtcNow, "start\nforged", "explicit cause"));
            string path = Path.Combine(root, "lifecycle.log");
            Assert.InRange(new FileInfo(path).Length, 1, 1024);
            Assert.DoesNotContain("\nforged", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
