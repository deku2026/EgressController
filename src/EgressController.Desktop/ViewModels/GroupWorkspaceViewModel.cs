using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private List<GroupRow> _groupRows = [];
    private bool _managingGroups, _maintainingGroups;
    private MacPolicyDocument? _appliedGroups;
    private CancellationTokenSource? _groupMaintenanceCancellation;
    private TaskCompletionSource? _groupMaintenanceCompletion;
    private readonly Dictionary<string, DateTimeOffset> _lastGroupTests = new(StringComparer.Ordinal);
    public bool ManagingGroups { get => _managingGroups; set => Set(ref _managingGroups, value); }
    public UiCommand ManageGroupsCommand { get; private set; } = null!;
    public IReadOnlyList<GroupRow> AllGroups => _groupRows;
    public IReadOnlyList<GroupRow> VisibleGroups => _groupRows.Where(row => row.Retained && (Matches(row.Name, NodeQuery)
        || GroupGraph.Leaves(row.Id, Document).Any(id => Matches(GroupGraph.Name(id, Document), NodeQuery)))).ToArray();
    public bool NoGroupsMatch => VisibleGroups.Count == 0;
    public string GroupCountLabel => $"{_groupRows.Count(row => row.Retained)} 个已保留分组 · {Document.Nodes.Count(node => node.IsSupported)} 个节点";
    public string AutomaticDownloadDescription => "下载出口自动从默认分组中选择测速成功的代理节点，跳过 DIRECT；无需另设下载代理。";
    private void RebuildGroups()
    {
        var previous = _groupRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        _groupRows = Document.Groups.Select(group => { if (previous.TryGetValue(group.Id, out var row)) { row.Update(group); return row; } return new GroupRow(this, group); }).ToList();
        PublishGroups();
    }
    private void PublishGroups()
    {
        foreach (var row in _groupRows) row.Refresh();
        foreach (string property in new[] { nameof(AllGroups), nameof(VisibleGroups), nameof(NoGroupsMatch), nameof(GroupCountLabel) }) Raise(property);
    }
    public void GroupRetentionChanged() { RebuildProjection(); StateChanged(); }
    public string? ResolveGroupMember(ProxyGroup group)
    {
        // Display the committed route while new observations are being applied or have failed.
        if (PreviewRunning && group.Kind is GroupKind.UrlTest or GroupKind.Fallback
            && _appliedGroups?.Groups.FirstOrDefault(item => item.Id == group.Id && item.Kind == group.Kind)?.SelectedMemberId is { } selected)
            return selected;
        return GroupSelector.Resolve(group.Id, Document, _probes, _backend.Now).NodeId;
    }
    public string ResolveGroupLabel(ProxyGroup group) => ResolveGroupMember(group) is { } id ? GroupGraph.Name(id, Document)
        : group.SelectedMemberId is { } selected && group.Kind == GroupKind.Select ? GroupGraph.Name(selected, Document) + " · 待检查" : "等待测速";
    public string MemberLatency(string id, string url)
    {
        if (id == GroupGraph.Direct) return "直连";
        if (id == GroupGraph.Reject) return "拒绝";
        if (Document.Groups.Any(group => group.Id == id)) id = GroupSelector.Resolve(id, Document, _probes, _backend.Now).NodeId ?? id;
        var latest = _probes.Where(probe => probe.NodeId == id && probe.TestUrl == url && probe.ObservedAt <= _backend.Now).MaxBy(probe => probe.ObservedAt);
        return latest is null ? "未测速" : _backend.Now - latest.ObservedAt > TimeSpan.FromMinutes(10) ? "已过期"
            : !latest.Available ? "超时" : $"{latest.LatencyMilliseconds:0} ms";
    }
    public string GroupLatency(ProxyGroup group) => ResolveGroupMember(group) is { } id ? MemberLatency(id, group.TestUrl) : "未就绪";
    private void MergeProbes(IEnumerable<GroupProbe> observations)
    {
        foreach (var probe in observations) { _probes.RemoveAll(old => old.NodeId == probe.NodeId && old.TestUrl == probe.TestUrl); _probes.Add(probe); }
        RefreshNodeLatencies(); PublishGroups(); Raise(nameof(ProxyResolvedName));
    }
    private static IReadOnlyList<RequiredGroupProbe> GroupRequests(IEnumerable<ProxyGroup> groups, MacPolicyDocument document) => groups
        .SelectMany(group => GroupGraph.Leaves(group.Id, document).Where(id => id != GroupGraph.Reject)
            .Select(id => new RequiredGroupProbe(id, group.TestUrl))).Distinct().ToArray();
    public Task TestGroupAsync(string id) => RunOperationAsync(async () =>
    {
        var group = Document.Groups.Single(group => group.Id == id);
        Notice = $"正在测试 {group.Name}…";
        var probes = await _backend.ProbeMembersAsync(CopyDocument(Document), CompileOptions(), GroupRequests([group], Document), ActionToken);
        MergeProbes(probes); _lastGroupTests[id] = _backend.Now;
        await ApplyAutomaticObservationsAsync(ActionToken);
        Notice = $"{group.Name} 测速完成 · {_backend.Now.ToLocalTime():HH:mm:ss}";
    });
    public Task SelectGroupMemberAsync(string groupId, string memberId) => RunOperationAsync(async () =>
    {
        var group = Document.Groups.Single(group => group.Id == groupId);
        if (!group.MemberIds.Contains(memberId) && memberId != GroupGraph.Direct) return;
        bool pending = HasChanges;
        if (!group.MemberIds.Contains(memberId)) group.MemberIds.Add(memberId);
        group.Kind = GroupKind.Select; group.StrategyOverridden = true; group.SelectedMemberId = memberId;
        StateChanged(); RaiseGroup(); PublishGroups();
        await _backend.SaveDraftAsync(CopyDocument(Document), ActionToken);
        if (!PreviewRunning || _appliedGroups is null || pending) { Notice = "分组选择已保存，连接后生效。"; return; }
        var candidate = CopyDocument(Document);
        var result = await _backend.ApplyAsync(candidate, CompileOptions(candidate), _prepared.Assets, ActionToken);
        if (result.Succeeded) { RememberAppliedGroups(candidate); HasChanges = false; Notice = "分组已切换，新连接立即使用所选出口。"; }
        else CurrentIssue = string.Join(" ", result.Diagnostics.Select(item => item.Message));
    });
    public Task SetGroupStrategyAsync(string id, GroupKind kind) => RunOperationAsync(async () =>
    {
        bool pending = HasChanges;
        var group = Document.Groups.Single(group => group.Id == id); group.Kind = kind; group.StrategyOverridden = true;
        StateChanged(); PublishGroups();
        var probes = await _backend.ProbeMembersAsync(CopyDocument(Document), CompileOptions(), GroupRequests([group], Document), ActionToken);
        MergeProbes(probes);
        await _backend.SaveDraftAsync(CopyDocument(Document), ActionToken);
        if (!PreviewRunning || _appliedGroups is null || pending) return;
        var candidate = CopyDocument(Document); var result = await _backend.ApplyAsync(candidate, CompileOptions(candidate), _prepared.Assets, ActionToken);
        if (result.Succeeded) { RememberAppliedGroups(candidate); HasChanges = false; Notice = "分组策略已生效，将按配置间隔自动检查。"; }
        else CurrentIssue = string.Join(" ", result.Diagnostics.Select(item => item.Message));
    });
    private void RememberAppliedGroups(MacPolicyDocument document)
    {
        _appliedGroups = CopyDocument(document);
        foreach (var group in _appliedGroups.Groups)
        {
            _lastGroupTests[group.Id] = _backend.Now;
            if (group.Kind is GroupKind.UrlTest or GroupKind.Fallback)
                group.SelectedMemberId = GroupSelector.Resolve(group.Id, document, _probes, _backend.Now).NodeId;
        }
        PublishGroups();
    }
    /// <summary>Independent policy timer. Status/connection observation failure is never an input to selection.</summary>
    public async Task MaintainAutomaticGroupsAsync()
    {
        if (_disposed || IsBusy || _maintainingGroups || !PreviewRunning || _appliedGroups is null) return;
        var due = _appliedGroups.Groups.Where(group => group.Kind is GroupKind.UrlTest or GroupKind.Fallback
            && (!_lastGroupTests.TryGetValue(group.Id, out var last) || _backend.Now - last >= TimeSpan.FromSeconds(Math.Max(1, group.IntervalSeconds)))).ToArray();
        if (due.Length == 0) return;
        _maintainingGroups = true;
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _groupMaintenanceCancellation = owned;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _groupMaintenanceCompletion = completion;
        try
        {
            var applied = _appliedGroups; var candidate = CopyDocument(applied);
            foreach (var group in due) _lastGroupTests[group.Id] = _backend.Now;
            var probes = await _backend.ProbeMembersAsync(candidate, CompileOptions(candidate), GroupRequests(due, candidate), owned.Token);
            if (!ReferenceEquals(applied, _appliedGroups) || !PreviewRunning || IsBusy) return;
            MergeProbes(probes);
            await ApplyAutomaticObservationsAsync(owned.Token);
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested) { }
        catch (Exception error) when (IsExpectedFailure(error))
        { Notice = "分组自动检查暂未完成，保留当前出口，稍后重试。"; }
        finally { _maintainingGroups = false; if (ReferenceEquals(_groupMaintenanceCancellation, owned)) _groupMaintenanceCancellation = null; completion.TrySetResult(); }
    }
    private async Task ApplyAutomaticObservationsAsync(CancellationToken cancellationToken)
    {
        if (!PreviewRunning || _appliedGroups is null) return;
        var candidate = CopyDocument(_appliedGroups);
        foreach (var group in candidate.Groups.Where(group => group.Kind is GroupKind.UrlTest or GroupKind.Fallback))
        {
            var selected = GroupSelector.Resolve(group.Id, candidate, _probes, _backend.Now);
            if (selected.IsSuccess) group.SelectedMemberId = selected.NodeId;
        }
        var options = CompileOptions(candidate);
        if (!XrayConfigCompiler.Compile(candidate, options).IsSuccess) return; // No silent DIRECT substitute.
        var result = await _backend.UpdateGroupsAsync(candidate, options, _prepared.Assets, cancellationToken);
        if (result.Succeeded)
        {
            _appliedGroups = candidate;
            foreach (var group in Document.Groups.Where(group => group.Kind is GroupKind.UrlTest or GroupKind.Fallback))
                if (candidate.Groups.FirstOrDefault(item => item.Id == group.Id) is { } current) group.SelectedMemberId = current.SelectedMemberId;
            if (!HasChanges) DslText = PolicyYaml.Write(Document);
            PublishGroups();
        }
    }
    private void InitializeImportedDefault()
    {
        // Only a new, unconfigured default is initialized. Never repair a saved missing reference by guessing.
        if (!string.IsNullOrEmpty(Document.Defaults.Global.TargetId)) return;
        var first = Document.Groups.FirstOrDefault(group => group.Retained && group.IsSupported
            && GroupGraph.Leaves(group.Id, Document).Any(id => Document.Nodes.Any(node => node.Id == id && node.IsSupported)));
        if (first is not null) Document.Defaults.Global = ExitChoice.Proxy(first.Id);
    }
}
