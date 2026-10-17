using System.Runtime.CompilerServices;
using EgressController.Diagnostics;

namespace EgressController.App;

public sealed partial class AppController
{
    public DiagnosticJournal Diagnostics { get; private set; } = null!;
    public string LogDirectory => Diagnostics.DirectoryPath;
    public string LastTunFailure => _singBox.Status.ErrorMessage ?? _singBox.LastFailure?.ErrorMessage
        ?? (IsTunRunning ? "" : _tunSupervisor.LastError) ?? "";

    public ControllerOperationResult ReportFailure(string stage, string code, string message, Exception? error = null)
    {
        Diagnostics.Report(stage, code, message, error);
        lock (_gate) _lastMessage = message;
        return new(false, message);
    }

    private ControllerOperationResult Failure(string message, Exception? error = null,
        [CallerMemberName] string operation = "")
        => ReportFailure(operation switch
        {
            nameof(StartTunAsync) or nameof(StopTunAsync) => "TUN 启停",
            nameof(CheckInternetHealthAsync) or nameof(RunInternetHealthCheckAsync) => "联网检测",
            nameof(CloseConnectionAsync) or nameof(CloseAllConnectionsAsync) => "连接管理",
            nameof(FlushDnsCacheAsync) => "DNS 缓存",
            _ => "配置与操作",
        }, operation, message, error);
}
