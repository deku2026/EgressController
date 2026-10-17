namespace EgressController.Transport.Upstream;

/// <summary>One deadline covers headers and body; streaming reads never inherit HttpClient.Timeout.</summary>
public static class HttpTransfer
{
    public static CancellationTokenSource Deadline(HttpClient client, CancellationToken token, TimeSpan? timeout = null)
    {
        TimeSpan duration = timeout ?? (client.Timeout == Timeout.InfiniteTimeSpan
            ? TimeSpan.FromSeconds(30) : client.Timeout);
        if (duration <= TimeSpan.Zero || duration == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(duration);
        return deadline;
    }

    public static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        byte[] buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return copied;
            if (read > limit - copied) throw new InvalidDataException($"remote response exceeds {limit} bytes");
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            copied += read;
        }
    }
}
