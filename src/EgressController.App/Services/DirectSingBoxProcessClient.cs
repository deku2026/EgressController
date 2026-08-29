using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using EgressController.SingBox.Runtime;

namespace EgressController.App.Services;

/// <summary>
/// Starts the managed core directly from the administrator App. The App manifest is the UAC
/// boundary, so a second elevated host and a named pipe are unnecessary for the sing-box child.
/// </summary>
public sealed class DirectSingBoxProcessClient : IElevatedHostClient
{
    private const int OutputCapacity = 256;
    private const int RecentOutputCapacity = 24;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Queue<string> _recentOutput = new();
    private Process? _process;
    private CancellationTokenSource? _outputLifetime;
    private Task? _outputPump;
    private Task? _stdoutReader;
    private Task? _stderrReader;
    private Task? _exitObserver;
    private Channel<SingBoxOutputEvent>? _outputChannel;
    private int _dropped;
    private bool _disposed;
    private ElevatedHostClientStatus _status = new(true, "stopped", null, 0, null, null);

    public event Action<SingBoxOutputEvent>? Output;

    public ElevatedHostClientStatus Status
    {
        get
        {
            lock (_stateGate)
                return _status;
        }
    }

    public async Task<ElevatedHostClientStatus> StartAsync(
        SingBoxRuntimeCandidate candidate,
        bool restart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null && IsAlive(_process))
            {
                if (!restart)
                    return Status;
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (_process is not null)
            {
                await DisposeProcessAsync().ConfigureAwait(false);
            }

            SetStatus(new ElevatedHostClientStatus(true, "starting", null, _dropped, null, null));
            string validationError = await ValidateCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (validationError.Length > 0)
                return Fail("candidate.invalid", validationError);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(candidate.CorePath),
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(candidate.CorePath)) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("run");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(Path.GetFullPath(candidate.ConfigPath));

            if (!process.Start())
                return Fail("process.start", "无法启动 sing-box。");

            _process = process;
            _dropped = 0;
            _outputChannel = Channel.CreateBounded<SingBoxOutputEvent>(new BoundedChannelOptions(OutputCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
            _outputLifetime = new CancellationTokenSource();
            CancellationToken outputToken = _outputLifetime.Token;
            ClearRecentOutput();
            _outputPump = PumpOutputAsync(outputToken);
            _stdoutReader = ReadOutputAsync(process.StandardOutput, "stdout", outputToken);
            _stderrReader = ReadOutputAsync(process.StandardError, "stderr", outputToken);
            _exitObserver = ObserveExitAsync(process, outputToken);

            // Catch invalid configs and immediate exits before reporting a successful start.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (!IsAlive(process))
            {
                int exitCode = TryGetExitCode(process);
                await DrainOutputReadersAsync().ConfigureAwait(false);
                string output = GetRecentOutput();
                await DisposeProcessAsync().ConfigureAwait(false);
                string detail = output.Length == 0 ? "请查看核心输出。" : "核心输出：" + output;
                return Fail("process.exited", $"sing-box 启动后立即退出，退出码 {exitCode}。{detail}");
            }

            SetStatus(new ElevatedHostClientStatus(true, "running", process.Id, _dropped, null, null));
            return Status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fail("process.start", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ElevatedHostClientStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(new ElevatedHostClientStatus(true, "stopped", null, _dropped, null, null));
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ElevatedHostClientStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process is not null && !IsAlive(_process))
            SetStatus(new ElevatedHostClientStatus(false, "stopped", null, _dropped, "process.exited", "sing-box 已退出。"));
        return Task.FromResult(Status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            await DisposeProcessAsync().ConfigureAwait(false);
        }
        _disposed = true;
        _gate.Dispose();
    }

    private async Task<string> ValidateCandidateAsync(
        SingBoxRuntimeCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(candidate.CorePath))
            return "sing-box 核心文件不存在：" + candidate.CorePath;
        if (!File.Exists(candidate.ConfigPath))
            return "sing-box 配置文件不存在：" + candidate.ConfigPath;
        if (!IsSha256(candidate.CoreSha256) || !IsSha256(candidate.ConfigSha256))
            return "sing-box 核心或配置 SHA-256 无效。";

        string coreHash = await ComputeSha256Async(candidate.CorePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(coreHash, candidate.CoreSha256, StringComparison.OrdinalIgnoreCase))
            return "sing-box 核心 SHA-256 与校验时不一致。";
        string configHash = await ComputeSha256Async(candidate.ConfigPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(configHash, candidate.ConfigSha256, StringComparison.OrdinalIgnoreCase))
            return "配置文件 SHA-256 与校验时不一致。";
        return string.Empty;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Process? process = _process;
        if (process is null)
            return;

        SetStatus(new ElevatedHostClientStatus(true, "stopping", process.Id, _dropped, null, null));
        try
        {
            if (IsAlive(process))
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The child exited between the state check and Kill.
        }
        finally
        {
            await DisposeProcessAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadOutputAsync(StreamReader reader, string source, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                RecordOutput(source, line);
                var output = new SingBoxOutputEvent(source, line, _dropped);
                if (!(_outputChannel?.Writer.TryWrite(output) ?? false))
                    Interlocked.Increment(ref _dropped);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        ChannelReader<SingBoxOutputEvent>? reader = _outputChannel?.Reader;
        if (reader is null)
            return;
        try
        {
            await foreach (SingBoxOutputEvent output in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                Output?.Invoke(output with { DroppedCount = _dropped });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ObserveExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(_process, process))
            {
                int exitCode = TryGetExitCode(process);
                SetStatus(new ElevatedHostClientStatus(false, "stopped", null, _dropped, "process.exited", $"sing-box 已退出，退出码 {exitCode}。"));
                Output?.Invoke(new SingBoxOutputEvent("lifecycle", $"sing-box exited with code {exitCode}.", _dropped));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task DisposeProcessAsync()
    {
        CancellationTokenSource? outputLifetime = _outputLifetime;
        _outputLifetime = null;
        outputLifetime?.Cancel();
        _outputChannel?.Writer.TryComplete();
        await DrainOutputReadersAsync().ConfigureAwait(false);
        if (_outputPump is not null)
        {
            try { await _outputPump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _outputPump = null;
        _stdoutReader = null;
        _stderrReader = null;
        _exitObserver = null;
        outputLifetime?.Dispose();
        _process?.Dispose();
        _process = null;
        _outputChannel = null;
    }

    private async Task DrainOutputReadersAsync()
    {
        Task[] readers = new[] { _stdoutReader, _stderrReader, _exitObserver }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (readers.Length == 0)
            return;
        try { await Task.WhenAll(readers).ConfigureAwait(false); } catch { }
    }

    private void ClearRecentOutput()
    {
        lock (_stateGate)
            _recentOutput.Clear();
    }

    private void RecordOutput(string source, string line)
    {
        string bounded = line.Length > 2_000 ? line[..2_000] + "…" : line;
        lock (_stateGate)
        {
            _recentOutput.Enqueue(source + ": " + bounded);
            while (_recentOutput.Count > RecentOutputCapacity)
                _recentOutput.Dequeue();
        }
    }

    private string GetRecentOutput()
    {
        lock (_stateGate)
            return string.Join(" | ", _recentOutput);
    }

    private ElevatedHostClientStatus Fail(string code, string message)
    {
        SetStatus(new ElevatedHostClientStatus(false, "failed", null, _dropped, code, message));
        return Status;
    }

    private void SetStatus(ElevatedHostClientStatus status)
    {
        lock (_stateGate)
            _status = status;
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static int TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
