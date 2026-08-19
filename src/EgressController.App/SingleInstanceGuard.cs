using System.Security.Cryptography;
using System.Text;

namespace EgressController.App;

/// <summary>
/// Per-user single-instance gate. A second launch only signals the first window; it does not
/// create a second router or touch System Proxy state.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    public static SingleInstanceGuard? Acquire()
    {
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Environment.UserName}|EgressController")))[..20];
        string mutexName = $"Local\\EgressController.App.Mutex.{suffix}";
        string eventName = $"Local\\EgressController.App.Activate.{suffix}";

        var mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            Signal(eventName);
            return null;
        }

        try
        {
            return new SingleInstanceGuard(
                mutex,
                new EventWaitHandle(initialState: false, EventResetMode.AutoReset, eventName));
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void StartActivationLoop(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        if (_listener is not null)
            return;

        _listener = Task.Run(() =>
        {
            WaitHandle[] waits = [_activation, _stop.Token.WaitHandle];
            while (!_stop.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits, 500) == 0)
                    activate();
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _activation.Set(); } catch (ObjectDisposedException) { }
        try { _listener?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _activation.Dispose();
        _stop.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }

    private static void Signal(string eventName)
    {
        try
        {
            using EventWaitHandle existing = EventWaitHandle.OpenExisting(eventName);
            existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance may still be between mutex acquisition and event creation.
        }
        catch (UnauthorizedAccessException)
        {
            // Per-user names should not normally hit this, but a second launch remains fail-closed.
        }
    }
}
