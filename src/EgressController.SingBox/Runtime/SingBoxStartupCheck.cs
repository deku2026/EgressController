namespace EgressController.SingBox.Runtime;

public static class SingBoxStartupCheck
{
    public static async Task<bool> WaitAsync(Func<CancellationToken, Task<string?>> probe,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        string lastError = "等待 sing-box 控制 API 响应";
        Exception? lastException = null;
        try
        {
            while (true)
            {
                try
                {
                    string? error = await probe(deadline.Token).ConfigureAwait(false);
                    if (error is null) { deadline.Token.ThrowIfCancellationRequested(); return true; }
                    lastError = error;
                    lastException = null;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw; }
                catch (Exception error) { lastError = error.Message; lastException = error; }
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SingBoxServiceException("tun.takeover", "TUN 启动检查超时：" + lastError, lastException);
        }
    }
}
