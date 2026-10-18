using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Api;

public sealed record ConnectionSample(SingBoxConnectionsResponse? Snapshot, long UploadRate, long DownloadRate, Exception? Error);

/// <summary>Observational only: no core lifecycle, protection, DNS or network-health dependencies.</summary>
public sealed class ConnectionSnapshotSampler(Func<CancellationToken, Task<SingBoxConnectionsResponse>> read, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private SingBoxConnectionsResponse? _previous;
    private long _observedAt;

    public async Task<ConnectionSample> SampleAsync(CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = await read(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            long now = _clock.GetTimestamp();
            double seconds = _previous is null ? 0 : _clock.GetElapsedTime(_observedAt, now).TotalSeconds;
            long Rate(long current, long previous)
            {
                if (seconds <= 0 || current < 0 || previous < 0 || current < previous) return 0;
                double rate = (current - previous) / seconds;
                return rate >= long.MaxValue ? long.MaxValue : (long)rate;
            }
            var sample = new ConnectionSample(snapshot,
                _previous is null ? 0 : Rate(snapshot.UploadTotal, _previous.UploadTotal),
                _previous is null ? 0 : Rate(snapshot.DownloadTotal, _previous.DownloadTotal), null);
            _previous = snapshot; _observedAt = now;
            return sample;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _previous = null;
            return new(null, 0, 0, error);
        }
    }
}
