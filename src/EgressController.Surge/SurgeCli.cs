using System.Diagnostics;
using EgressController.Core.Protection;

namespace EgressController.Surge;

public sealed record ConfigValidation(bool Valid, string Summary);
public interface IConfigValidator { Task<ConfigValidation> CheckAsync(string path, CancellationToken token); }
public sealed class SurgeCli(string executable = "/Applications/Surge.app/Contents/Applications/surge-cli") : IConfigValidator
{
    public async Task<ConfigValidation> CheckAsync(string path, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        if (!File.Exists(executable)) return new(false, "未找到 Surge CLI，请在设置中选择已安装的 Surge。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--check"); start.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(start) ?? throw new IOException("无法启动配置检查器。");
        // Drain without forwarding private node definitions to UI/logs.
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new(process.ExitCode == 0, process.ExitCode == 0 ? "Surge 校验通过 · 尚未应用" : $"Surge 校验失败（退出码 {process.ExitCode}）；保留上次有效配置。");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(); // Only our offline checker, never Surge/Core or target apps.
            if (token.IsCancellationRequested) throw;
            return new(false, "Surge CLI 校验超过 30 秒；当前网络配置未改变。");
        }
    }
}
