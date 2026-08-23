using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using EgressController.SingBox.Runtime;

namespace EgressController.App.Services;

public sealed record ElevatedHostLaunchOptions
{
    public required string HostExecutablePath { get; init; }
    public required string PipeName { get; init; }
    public required string DataRoot { get; init; }
    public string? AllowedSystemCorePath { get; init; }
    public int ClientProcessId { get; init; } = Environment.ProcessId;
}

/// <summary>
/// Starts one session-scoped ElevatedHost. The App requests administrator execution, so the host
/// normally starts directly with the same token; the runas fallback keeps dotnet-run/dev launches
/// usable when the App executable is not the process carrying the manifest.
/// </summary>
public sealed class ElevatedHostLauncher : IAsyncDisposable
{
    private readonly object _gate = new();
    private NamedPipeElevatedHostClient? _client;
    private Process? _hostProcess;

    public async Task<NamedPipeElevatedHostClient> GetOrStartAsync(
        ElevatedHostLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_client is not null)
                return _client;
        }

        string hostPath = Path.GetFullPath(options.HostExecutablePath);
        if (!File.Exists(hostPath))
            throw new ElevatedHostLaunchException("host.missing", "ElevatedHost 可执行文件不存在。", null);
        if (!Path.IsPathRooted(options.DataRoot))
            throw new ElevatedHostLaunchException("host.data-root", "ElevatedHost data root 必须是绝对路径。", null);

        string arguments = BuildArguments(options with
        {
            HostExecutablePath = hostPath,
            DataRoot = Path.GetFullPath(options.DataRoot),
        });
        Process process;
        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(hostPath, arguments, IsCurrentProcessElevated());
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ElevatedHost。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ElevatedHostLaunchException("uac.cancelled", "用户取消了 ElevatedHost 提权。", ex);
        }
        catch (Win32Exception ex)
        {
            throw new ElevatedHostLaunchException("uac.failed", "ElevatedHost 提权失败。", ex);
        }

        var client = new NamedPipeElevatedHostClient(options.PipeName);
        lock (_gate)
        {
            if (_client is not null)
            {
                process.Dispose();
                return _client;
            }
            _client = client;
            _hostProcess = process;
            return client;
        }
    }

    public static string BuildArguments(ElevatedHostLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PipeName)
            || string.IsNullOrWhiteSpace(options.DataRoot)
            || options.ClientProcessId <= 0)
            throw new ArgumentException("ElevatedHost launch arguments are incomplete.", nameof(options));

        var parts = new List<string>
        {
            "--pipe", Quote(options.PipeName),
            "--client-pid", options.ClientProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--data-root", Quote(Path.GetFullPath(options.DataRoot)),
        };
        if (!string.IsNullOrWhiteSpace(options.AllowedSystemCorePath))
        {
            parts.Add("--system-core");
            parts.Add(Quote(Path.GetFullPath(options.AllowedSystemCorePath)));
        }
        return string.Join(' ', parts);
    }

    internal static ProcessStartInfo CreateStartInfo(string hostPath, string arguments, bool isElevated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(hostPath) ?? Environment.CurrentDirectory,
        };
        if (isElevated)
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
        }
        else
        {
            startInfo.Verb = "runas";
            startInfo.UseShellExecute = true;
        }
        return startInfo;
    }

    public async ValueTask DisposeAsync()
    {
        NamedPipeElevatedHostClient? client;
        Process? process;
        lock (_gate)
        {
            client = _client;
            process = _hostProcess;
            _client = null;
            _hostProcess = null;
        }
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
        process?.Dispose();
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        if (value.Any(char.IsWhiteSpace) || value.Contains('"'))
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    private static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed class ElevatedHostLaunchException(string code, string message, Exception? inner)
    : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
}
