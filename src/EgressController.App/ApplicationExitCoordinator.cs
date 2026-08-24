using Avalonia.Controls;

namespace EgressController.App;

internal static class ApplicationClosePolicy
{
    public static bool ShouldHideToTray(bool exitRequested, WindowCloseReason reason)
        => !exitRequested && reason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;
}

internal sealed class ApplicationExitCoordinator
{
    private int _started;

    public async Task ExitAsync(
        Func<Task> stopTunAsync,
        Action allowWindowClose,
        Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(stopTunAsync);
        ArgumentNullException.ThrowIfNull(allowWindowClose);
        ArgumentNullException.ThrowIfNull(shutdown);

        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        try
        {
            await stopTunAsync();
        }
        catch
        {
            // AppController disposal gets a final chance to stop the managed core.
        }
        finally
        {
            try
            {
                allowWindowClose();
            }
            finally
            {
                shutdown();
            }
        }
    }
}
