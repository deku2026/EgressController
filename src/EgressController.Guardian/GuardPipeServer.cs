using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace EgressController.Guardian;

public sealed class GuardPipeServer(GuardPaths paths, GuardRegistration registration, GuardRuntime runtime, Action wake)
{
    public async Task RunAsync(CancellationToken token)
    {
        var clients = new ConcurrentDictionary<long, Task>();
        long next = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = GuardPipeSecurity.CreateServer(paths);
                try { await pipe.WaitForConnectionAsync(token).ConfigureAwait(false); }
                catch { pipe.Dispose(); throw; }
                long id = ++next;
                var task = HandleAsync(pipe, token);
                clients[id] = task;
                _ = task.ContinueWith(_ => { clients.TryRemove(id, out var ignored); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients.Values).ConfigureAwait(false); }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using (pipe)
        {
            try
            {
                var peer = GuardPipeSecurity.Client(pipe, paths);
                var mailbox = new GuardPolicyMailbox(paths.DataDirectory);
                using var handler = new GuardRequestHandler(peer, registration.MainExecutable, runtime, mailbox.Load, wake);
                while (!token.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(6));
                    var request = await GuardWire.ReadAsync(pipe, GuardJson.Default.GuardRequest, deadline.Token).ConfigureAwait(false);
                    await GuardWire.WriteAsync(pipe, handler.Handle(request), GuardJson.Default.GuardReply, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException
                or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
    }
}
