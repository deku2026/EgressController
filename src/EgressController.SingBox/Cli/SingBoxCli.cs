using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EgressController.SingBox.Cli;

public sealed record SingBoxVersionInfo
{
    public required Version Version { get; init; }
    public required string RawOutput { get; init; }
}

public sealed record SingBoxCommandResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public bool Succeeded => ExitCode == 0;
}

public interface ISingBoxCli
{
    Task<SingBoxVersionInfo> GetVersionAsync(string executablePath, CancellationToken cancellationToken = default);
    Task<SingBoxCommandResult> CheckAsync(string executablePath, string configPath, CancellationToken cancellationToken = default);
}

public sealed partial class SingBoxCli : ISingBoxCli
{
    private readonly TimeSpan _timeout;

    public SingBoxCli(TimeSpan? timeout = null)
        => _timeout = timeout ?? TimeSpan.FromSeconds(20);

    public async Task<SingBoxVersionInfo> GetVersionAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        SingBoxCommandResult result = await RunAsync(executablePath, ["version"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new SingBoxCliException($"sing-box version 失败：{result.StandardError}", result);

        string output = string.Join(Environment.NewLine, result.StandardOutput, result.StandardError);
        Match match = VersionRegex().Match(output);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out Version? version))
            throw new SingBoxCliException("无法从 sing-box version 输出解析版本。", result);
        return new SingBoxVersionInfo { Version = version, RawOutput = output };
    }

    public Task<SingBoxCommandResult> CheckAsync(
        string executablePath,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("config path is required", nameof(configPath));
        return RunAsync(executablePath, ["check", "-c", configPath], cancellationToken);
    }

    public async Task<SingBoxCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("sing-box executable path is required", nameof(executablePath));
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("sing-box executable does not exist", executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new SingBoxCliException("无法启动 sing-box。");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new SingBoxCommandResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = await stdout.ConfigureAwait(false),
                StandardError = await stderr.ConfigureAwait(false),
            };
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
    }

    [GeneratedRegex(@"sing-box\s+version\s+v?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}

public sealed class SingBoxCliException(string message, SingBoxCommandResult? result = null)
    : InvalidOperationException(message)
{
    public SingBoxCommandResult? Result { get; } = result;
}
