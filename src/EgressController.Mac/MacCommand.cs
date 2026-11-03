using System.Diagnostics;
using EgressController.Core.Protection;

namespace EgressController.Mac;

internal static class MacCommand
{
    public static async Task<string> ReadAsync(string file, string[] arguments, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法读取系统信息。");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("系统信息读取失败。");
            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("系统信息读取超时。");
        }
    }
}
