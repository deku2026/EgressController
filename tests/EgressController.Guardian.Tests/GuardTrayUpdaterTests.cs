namespace EgressController.Guardian.Tests;

public sealed class GuardTrayUpdaterTests
{
    [Fact]
    public void Matching_payload_does_not_stop_or_replace_any_process()
    {
        var platform = new Fake { Current = true };
        Assert.False(GuardTrayUpdater.EnsureCurrent(platform));
        Assert.Equal(["compare"], platform.Calls);
    }
    [Fact]
    public void Update_validates_before_stopping_and_keeps_the_same_installation()
    {
        var platform = new Fake();
        Assert.True(GuardTrayUpdater.EnsureCurrent(platform));
        Assert.Equal(["compare", "stage", "stop-tray", "publish", "cleanup"], platform.Calls);
    }
    [Theory]
    [InlineData("stage")]
    [InlineData("stop-tray")]
    [InlineData("publish")]
    public void Failures_restore_and_cleanup_without_a_service_restart(string failure)
    {
        var platform = new Fake { Failure = failure };
        Assert.Throws<IOException>(() => GuardTrayUpdater.EnsureCurrent(platform));
        Assert.Contains("restore", platform.Calls);
        Assert.Equal("cleanup", platform.Calls.Last());
        if (failure == "stage") Assert.DoesNotContain("stop-tray", platform.Calls);
        if (failure != "publish") Assert.DoesNotContain("publish", platform.Calls);
    }
    [Fact]
    public void Payload_comparison_checks_bytes_not_timestamp_or_version_text()
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController.TrayPayloadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "EgressController.GuardTray.exe");
            File.WriteAllText(file, "old");
            var time = File.GetLastWriteTimeUtc(file);
            string old = WindowsGuardTrayUpdate.PayloadDigest(root);
            File.WriteAllText(file, "new"); File.SetLastWriteTimeUtc(file, time);
            Assert.NotEqual(old, WindowsGuardTrayUpdate.PayloadDigest(root));
            File.WriteAllText(Path.Combine(root, "renderer.dll"), "mock");
            Assert.NotEqual(old, WindowsGuardTrayUpdate.PayloadDigest(root));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Fake : IGuardTrayUpdatePlatform
    {
        public bool Current;
        public string? Failure;
        public List<string> Calls = [];
        public bool NeedsUpdate() { Record("compare"); return !Current; }
        public void Stage() => Record("stage");
        public void StopInstalledTray() => Record("stop-tray");
        public void Publish() => Record("publish");
        public void Restore() => Record("restore");
        public void Cleanup() => Record("cleanup");
        private void Record(string operation) { Calls.Add(operation); if (operation == Failure) throw new IOException(operation); }
    }
}
