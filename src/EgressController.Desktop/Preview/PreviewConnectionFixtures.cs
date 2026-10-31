using EgressController.Desktop.Services;
using EgressController.Policy;

namespace EgressController.Desktop.Preview;

/// <summary>Synthetic observer data, available only after explicit preview/test isolation.</summary>
public static class PreviewConnectionFixtures
{
    public static ConnectionWorkspaceSnapshot Create()
    {
        PreviewEnvironment.RequireIsolated();
        var now = new DateTimeOffset(2026, 10, 31, 13, 42, 0, TimeSpan.Zero);
        string[] apps = ["Claude", "Google Chrome", "Cursor", "Safari", "Slack", "Spotify"];
        string[] hosts = ["api.anthropic.example.invalid", "assets.web.example.invalid", "extensions.editor.example.invalid", "sync.browser.example.invalid", "files.workspace.example.invalid", "audio.stream.example.invalid"];
        var records = Enumerable.Range(0, 180).Select(index => new ConnectionRecord("connection-" + index, now.AddSeconds(-index * 3))
        {
            ApplicationId = "app-" + index % 6, ApplicationName = apps[index % 6], ExecutablePath = index == 2 ? null : "/Applications/" + apps[index % 6] + ".app/Contents/MacOS/" + apps[index % 6], ProcessId = index == 2 ? null : 3200 + index % 6,
            Host = "session-" + index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "." + hosts[index % 6], RemoteIp = index == 2 ? null : "192.0.2." + (index % 200 + 1), RemotePort = 443, Transport = index % 5 == 0 ? "udp" : "tcp",
            StartedAt = index == 2 ? null : now.AddSeconds(-index * 3 - 24), EndedAt = index % 3 == 1 ? now.AddSeconds(-index * 3) : null,
            Activity = index % 3 == 0 ? ConnectionActivity.Active : index % 3 == 1 ? ConnectionActivity.Closed : ConnectionActivity.Unknown,
            ObservedExitKind = index % 3 == 0 ? ExitKind.Proxy : index % 3 == 1 ? ExitKind.NonHome : ExitKind.Home, ObservedTargetName = index % 3 == 0 ? "Workspace Auto" : null, ObservedInterface = index % 3 == 2 ? "en5" : "en0",
            MatchedRule = index == 2 ? null : index % 2 == 0 ? "应用规则 · " + apps[index % 6] : "DOMAIN-SUFFIX · " + hosts[index % 6],
            UploadedBytes = index == 2 ? null : 16_384 + index * 491, DownloadedBytes = index == 2 ? null : 524_288 + index * 70_281,
            UploadBytesPerSecond = index % 3 == 0 ? 1800 + index * 21 : null, DownloadBytesPerSecond = index % 3 == 0 ? 48_100 + index * 124 : null
        }).ToArray();
        return new(records, now, true, "仅展示观察器明确提供的事实；未知存活状态不计入活跃连接，未解密请求内容。");
    }
}
