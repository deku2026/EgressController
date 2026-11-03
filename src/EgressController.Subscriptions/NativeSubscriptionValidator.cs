using System.Diagnostics;
using EgressController.Core.Protection;

namespace EgressController.Subscriptions;

public sealed class NativeSubscriptionValidator(string surgeCli, string mihomo) : ISubscriptionValidator
{
    public async Task ValidateAsync(SourceFormat format, string path, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        if (format == SourceFormat.Surge)
        {
            _ = SurgeSource.Parse(await File.ReadAllTextAsync(path, new System.Text.UTF8Encoding(false, true), token).ConfigureAwait(false));
            if (!OperatingSystem.IsMacOS()) return; // Windows has no Surge CLI. UI explicitly says structural validation.
        }
        string executable = format == SourceFormat.Surge ? surgeCli : mihomo;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new IOException(format == SourceFormat.Surge ? "请配置已安装的 Surge CLI 路径。" : "请配置 Mihomo 校验器路径（仅执行 -t，不启动网络）。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (format == SourceFormat.Surge) { start.ArgumentList.Add("--check"); start.ArgumentList.Add(path); }
        else { start.ArgumentList.Add("-t"); start.ArgumentList.Add("-d"); start.ArgumentList.Add(Path.GetDirectoryName(path)!); start.ArgumentList.Add("-f"); start.ArgumentList.Add(path); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(start) ?? throw new IOException("无法启动订阅校验器。");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException($"{format} 订阅未通过原生语法校验；保留上次有效文件。");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException($"{format} 订阅校验超时；未启动任何代理内核。");
        }
    }
}
