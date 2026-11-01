namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private readonly SemaphoreSlim _draftWriter = new(1, 1);
    private CancellationTokenSource? _draftDelay;
    private long _savedDraftRevision = -1;
    private void ScheduleDraftSave()
    {
        if (IsPreview || !_initialized || _closing) return;
        _draftDelay?.Cancel();
        _ = SaveAfterQuietPeriodAsync();
    }
    private async Task SaveAfterQuietPeriodAsync()
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _draftDelay = owned;
        try { await Task.Delay(350, owned.Token); await PersistDraftAsync(owned.Token); }
        catch (OperationCanceledException) when (owned.IsCancellationRequested) { }
        catch (Exception error) when (IsExpectedFailure(error))
        { RecordFailure("draft.save", error); CurrentIssue = "配置草稿未能保存，请检查日志并重试保存。"; }
        finally { if (ReferenceEquals(_draftDelay, owned)) _draftDelay = null; }
    }
    private async Task PersistDraftAsync(CancellationToken cancellationToken = default)
    {
        await _draftWriter.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized || _savedDraftRevision == _revision) return;
            long revision = _revision;
            var snapshot = CopyDocument(Document);
            await _backend.SaveDraftAsync(snapshot, cancellationToken);
            _savedDraftRevision = revision;
        }
        finally { _draftWriter.Release(); }
    }
    public void ReportShutdownFailure()
    {
        _closing = false; Raise(nameof(CanEdit));
        CurrentIssue = "网络恢复尚未确认，窗口保持打开。请打开日志目录检查，或再次关闭以重试恢复。";
        Notice = "退出未完成；正在保留网络恢复所需的助手连接。";
    }
}
