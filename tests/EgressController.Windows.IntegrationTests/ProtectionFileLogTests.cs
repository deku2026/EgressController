using EgressController.Diagnostics;

namespace EgressController.Windows.IntegrationTests;

public sealed class ProtectionFileLogTests
{
    [Fact]
    public void Only_protection_and_lifecycle_causes_reach_the_file_writer()
    {
        var written = new List<CoreLogEntry>();
        var journal = new DiagnosticJournal(Path.GetFullPath("mock-protection.log"), written.Add,
            shouldPersist: ProtectionLogPolicy.IsRelevant);
        journal.Report("sing-box", "core.output", "ordinary connection error");
        journal.Report("连接监控", "monitor.connections", "display sample failed");
        journal.Info("联网检测", "first attempt failed, waiting for retry");
        Assert.Empty(written);
        journal.Info("TUN 启动", "explicit configuration changed");
        journal.Report("联网检测", "network.failed", "three consecutive failures");
        journal.Info("进程终止", "matched saved EXE path");
        journal.Report("进程保护", "protection.sweep", "cannot inspect selected process");
        journal.Info("守护管理", "service stopped by user");
        Assert.Equal(5, written.Count);
        foreach (string stage in new[] { "应用扫描", "初始化", "托盘异常" })
        {
            journal.Info(stage, "routine progress");
            journal.Report(stage, "startup.failed", "cannot finish startup or keep the tray alive");
        }
        Assert.Equal(8, written.Count);
        Assert.DoesNotContain(written, entry => entry.Message == "routine progress");
        Assert.DoesNotContain(written, entry => entry.Message.Contains("ordinary connection"));
        Assert.Contains(journal.Snapshot(), item => item.Code == "monitor.connections");
    }
}
