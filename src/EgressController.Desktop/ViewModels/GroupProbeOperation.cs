using Avalonia.Threading;
using EgressController.Policy;
namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private CancellationTokenSource? _probeCancellation;
    private TaskCompletionSource? _probeCompletion;
    private string? _testingGroup;
    private int _probeDone, _probeTotal;
    public bool IsProbing => _probeCancellation is not null;
    public bool CanProbe => CanEdit && !IsProbing;
    public string ProbeProgress => IsProbing ? $"HTTP 延迟 · {_probeDone}/{_probeTotal} · 非家宽直连" : "HTTP 延迟 · 非家宽直连";
    public UiCommand CancelProbeCommand => new(() => _probeCancellation?.Cancel());
    public bool GroupIsTesting(string id) => IsProbing && (_testingGroup is null || _testingGroup == id);
    private async Task<bool> RunGroupProbeAsync(string? groupId)
    {
        if (_closing || _disposed || IsBusy || IsProbing) return false;
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probeCancellation = owned; _probeCompletion = completion; _testingGroup = groupId; _probeDone = 0;
        long revision = _revision;
        try
        {
            if (_maintainingGroups)
            {
                _groupMaintenanceCancellation?.Cancel();
                if (_groupMaintenanceCompletion is { } maintenance) await maintenance.Task;
            }
            owned.Token.ThrowIfCancellationRequested();
            var snapshot = CopyDocument(Document); var options = CompileOptions(snapshot);
            var groups = snapshot.Groups.Where(group => group.IsSupported && (groupId is null ? group.Retained : group.Id == groupId)).ToArray();
            var requests = GroupRequests(groups, snapshot); _probeTotal = requests.Count;
            PublishProbeState(); Notice = "正在测量 HTTP 延迟，可以继续浏览或取消。";
            var progress = new ProbeProgressSink(batch =>
            {
                if (owned.IsCancellationRequested || !ReferenceEquals(_probeCancellation, owned) || revision != _revision) return;
                _probeDone += batch.Count; MergeProbes(batch); Raise(nameof(ProbeProgress));
            });
            var results = await _backend.ProbeMembersWithProgressAsync(snapshot, options, requests, progress, owned.Token);
            owned.Token.ThrowIfCancellationRequested();
            if (revision != _revision) { Notice = "配置已变化，本轮剩余结果已丢弃。"; return false; }
            MergeProbes(results);
            foreach (var group in groups) _lastGroupTests[group.Id] = _backend.Now;
            await ApplyAutomaticObservationsAsync(owned.Token);
            Notice = $"HTTP 延迟已测量 · {results.Count(result => result.Available)}/{results.Count} 可连接 · 非家宽出口";
            return true;
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested) { Notice = "测速已取消，已完成的结果保留。"; }
        catch (Exception error) when (IsExpectedFailure(error)) { RecordFailure("group.probe", error); CurrentIssue = SafeFailure(error, "延迟测速未完成，请检查非家宽出口与日志。"); }
        finally { _probeCancellation = null; _testingGroup = null; PublishProbeState(); completion.TrySetResult(); }
        return false;
    }
    private void PublishProbeState()
    {
        Raise(nameof(IsProbing)); Raise(nameof(CanProbe)); Raise(nameof(ProbeProgress));
        foreach (var row in _groupRows) row.Refresh();
    }
    private sealed class ProbeProgressSink(Action<IReadOnlyList<GroupProbe>> action) : IProgress<GroupProbe>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<GroupProbe> _pending = new();
        private int _scheduled;
        public void Report(GroupProbe value)
        {
            _pending.Enqueue(value); Schedule();
        }
        private void Schedule()
        {
            if (Interlocked.Exchange(ref _scheduled, 1) == 0) Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);
        }
        private void Drain()
        {
            var batch = new List<GroupProbe>(); while (_pending.TryDequeue(out var probe)) batch.Add(probe);
            Interlocked.Exchange(ref _scheduled, 0);
            if (batch.Count > 0) action(batch);
            if (!_pending.IsEmpty) Schedule();
        }
    }
}
