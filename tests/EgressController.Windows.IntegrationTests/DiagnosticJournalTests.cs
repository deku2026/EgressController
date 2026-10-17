using EgressController.App.ViewModels;
using EgressController.Diagnostics;

namespace EgressController.Windows.IntegrationTests;

public sealed class DiagnosticJournalTests
{
    [Fact]
    public void Retry_and_success_messages_keep_failure_details_and_count_repeated_errors()
    {
        var written = new List<CoreLogEntry>();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var journal = new DiagnosticJournal(Path.GetFullPath("mock-controller.log"), written.Add, () => now);
        var vm = new DiagnosticsViewModel(journal, new(), "mock-sing-box.log");
        var error = new IOException("mock interface disappeared", new InvalidOperationException("mock adapter GUID"));
        journal.Report("TUN", "tun.takeover", error.Message, error);
        vm.Refresh();
        Assert.True(vm.HasFailures);
        Assert.Contains("mock adapter GUID", vm.SelectedDetails);
        journal.Info("TUN", "Preparing");
        journal.Report("TUN", "tun.takeover", error.Message, error);
        journal.Info("TUN", "Running");
        vm.Refresh();
        Assert.Equal(2, Assert.Single(vm.Issues).Issue.Count);
        Assert.Contains("mock interface disappeared", vm.LatestFailure);
        Assert.Contains("Running", vm.ControllerLog);
        Assert.Contains(journal.LogPath, vm.ExportText);
        Assert.Single(written, entry => entry.Level == "error");
        now += TimeSpan.FromSeconds(31);
        journal.Report("TUN", "tun.takeover", error.Message, error);
        Assert.Equal(2, written.Count(entry => entry.Level == "error"));
    }

    [Fact]
    public void Log_permission_failure_is_visible_without_throwing_or_losing_original_failure()
    {
        var journal = new DiagnosticJournal(Path.GetFullPath("mock-controller.log"),
            _ => throw new UnauthorizedAccessException("mock write denied"));
        journal.Report("配置", "profile.save", "mock disk full");
        var vm = new DiagnosticsViewModel(journal, new(), "core.log");
        vm.Refresh();
        Assert.Contains(vm.Issues, item => item.Issue.Code == "profile.save");
        Assert.Contains(vm.Issues, item => item.Issue.Code == "log.write");
        Assert.Contains("mock write denied", vm.SelectedDetails);
        Assert.Contains(journal.LogPath, vm.SelectedDetails);
    }

    [Theory]
    [InlineData("Authorization: Bearer token-value", "token-value")]
    [InlineData("{\"controllerSecret\":\"private-value\"}", "private-value")]
    [InlineData("secret = private-value", "private-value")]
    [InlineData("password: private-value", "private-value")]
    public void Exported_failures_redact_credentials(string message, string credential)
    {
        var written = new List<CoreLogEntry>();
        var journal = new DiagnosticJournal(Path.GetFullPath("mock-controller.log"), written.Add);
        journal.Report("API", "api.error", message, new IOException(message));
        var vm = new DiagnosticsViewModel(journal, new(), "core.log");
        vm.Refresh();
        Assert.DoesNotContain(credential, vm.ExportText);
        Assert.All(written, entry => Assert.DoesNotContain(credential, entry.Message));
        Assert.Contains("[redacted]", vm.SelectedDetails);
    }

    [Fact]
    public async Task Concurrent_errors_remain_bounded_and_do_not_hide_recent_failures()
    {
        var journal = new DiagnosticJournal(Path.GetFullPath("mock-controller.log"), _ => { }, capacity: 10);
        await Task.WhenAll(Enumerable.Range(0, 80).Select(index => Task.Run(() =>
            journal.Report("mock", "test." + index, new string('x', 40000)), TestContext.Current.CancellationToken)));
        Assert.Equal(10, journal.Snapshot().Count);
        Assert.All(journal.Snapshot(), issue => Assert.True(issue.Detail.Length < 33000));
        journal.Report("last", "last.error", "last failure");
        Assert.Equal("last.error", journal.Snapshot()[0].Code);
    }

    [Fact]
    public void File_logs_preserve_error_details_across_reopening_and_rotate_with_a_bound()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-log-mock-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "controller.log");
        try
        {
            var writer = new RotatingDiagnosticFile(path, maxBytes: 1400);
            var journal = new DiagnosticJournal(path, writer.Append);
            journal.Report("启动", "tun.takeover", "mock failure", new IOException("mock stack detail"));
            Assert.Contains("mock stack detail", File.ReadAllText(path));
            new DiagnosticJournal(path, writer.Append).Info("重试", "mock retry");
            Assert.Contains("mock stack detail", File.ReadAllText(path));
            for (int i = 0; i < 20; i++) journal.Info("output", i + new string('x', 350));
            Assert.True(File.Exists(path + ".1"));
            Assert.Equal(2, Directory.GetFiles(root).Length);
            Assert.True(new FileInfo(path).Length <= 1400);
            Assert.True(new FileInfo(path + ".1").Length <= 1400);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
