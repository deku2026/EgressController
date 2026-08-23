using System.IO.Pipes;
using EgressController.Core.Ipc;
using EgressController.ElevatedHost;

namespace EgressController.ElevatedHost.Tests;

public sealed class ElevatedHostServerIntegrationTests
{
    [Fact]
    public async Task Named_pipe_checks_client_pid_and_handles_hello_status_and_shutdown()
    {
        string pipeName = "EgressController.Tests." + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "EgressController.HostServerTests", Guid.NewGuid().ToString("N"));
        var host = new FakeHost();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var server = new ElevatedHostServer(
            pipeName,
            Environment.ProcessId,
            new ElevatedHostPathPolicy { DataRoot = root },
            host);
        Task serverTask = server.RunAsync(cancellation.Token);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(connectTimeout.Token);

        ElevatedIpcMessage hello = ElevatedIpcMessage.Request(ElevatedIpcKind.Hello, Environment.ProcessId);
        await ElevatedIpcProtocol.WriteAsync(client, hello, TestContext.Current.CancellationToken);
        ElevatedIpcMessage response = (await ElevatedIpcProtocol.ReadAsync(client, TestContext.Current.CancellationToken))!;
        Assert.Equal(ElevatedIpcKind.Response, response.Kind);
        Assert.Null(response.ErrorCode);
        Assert.Equal("stopped", response.State);

        ElevatedIpcMessage statusRequest = ElevatedIpcMessage.Request(ElevatedIpcKind.GetStatus, Environment.ProcessId);
        await ElevatedIpcProtocol.WriteAsync(client, statusRequest, TestContext.Current.CancellationToken);
        ElevatedIpcMessage status = (await ElevatedIpcProtocol.ReadAsync(client, TestContext.Current.CancellationToken))!;
        Assert.Equal(statusRequest.RequestId, status.RequestId);
        Assert.Equal("stopped", status.State);

        ElevatedIpcMessage shutdown = ElevatedIpcMessage.Request(ElevatedIpcKind.Shutdown, Environment.ProcessId);
        await ElevatedIpcProtocol.WriteAsync(client, shutdown, TestContext.Current.CancellationToken);
        ElevatedIpcMessage shutdownResponse = (await ElevatedIpcProtocol.ReadAsync(client, TestContext.Current.CancellationToken))!;
        Assert.Null(shutdownResponse.ErrorCode);
        await serverTask.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(host.StopCount > 0);
    }

    private sealed class FakeHost : ISingBoxProcessHost
    {
        public event Action<SingBoxOutputLine>? Output
        {
            add { }
            remove { }
        }

        public int StopCount { get; private set; }
        public SingBoxHostStatus Status { get; } = new("stopped", null, 0, null, null);

        public Task<SingBoxHostStatus> StartAsync(ElevatedIpcMessage request, CancellationToken cancellationToken = default)
            => Task.FromResult(Status);

        public Task<SingBoxHostStatus> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(Status);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
