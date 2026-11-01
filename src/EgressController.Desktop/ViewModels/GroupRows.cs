using EgressController.Policy;
using EgressController.Desktop.Presentation.Controls;
namespace EgressController.Desktop.ViewModels;

public sealed class GroupRow : Bindable
{
    private readonly ShellViewModel _owner;
    private bool _expanded, _showUnavailable;
    private IReadOnlyList<GroupMemberRow> _members = [];
    public ProxyGroup Model { get; private set; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Product => GroupArtwork.Product(Model.Name, Model.Icon);
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) { ReconcileMembers(); Raise(nameof(Chevron)); } } }
    public string Chevron => Expanded ? "chevron-up" : "chevron-down";
    public bool IsDefault => _owner.Document.Defaults.Global.TargetId == Id;
    public bool CanRemove => !IsDefault && !_owner.Document.Applications.Any(app => app.Via.TargetId == Id) && !_owner.Document.RuleSets.Any(rule => rule.Via.TargetId == Id);
    public bool Retained { get => Model.Retained; set { if (value == Model.Retained || !value && !CanRemove) return; Model.Retained = value; _owner.GroupRetentionChanged(); Raise(); } }
    public string Kind => Model.Kind switch { GroupKind.Select => "手动选择", GroupKind.UrlTest => "自动优选", GroupKind.Fallback => "故障转移", _ => "不支持" };
    public string Detail => !Model.IsSupported ? string.Join(" · ", Model.Diagnostics.Select(item => item.Message)) : $"{Model.MemberIds.Count} 个成员 · {(Model.Kind == GroupKind.Select ? "选择后即时生效" : $"每 {Model.IntervalSeconds} 秒检查")} · 非家宽出口";
    public string Current => _owner.ResolveGroupLabel(Model);
    public string Latency => IsTesting ? "测速中" : _owner.GroupLatency(Model);
    public bool IsTesting => _owner.GroupIsTesting(Id);
    public string MemberCount => Model.MemberIds.Count.ToString();
    public bool ShowUnavailable { get => _showUnavailable; set { if (Set(ref _showUnavailable, value)) ReconcileMembers(); } }
    public IReadOnlyList<GroupMemberRow> Members => _members;
    public UiCommand ToggleCommand { get; }
    public AsyncUiCommand AutomaticCommand { get; }
    public AsyncUiCommand FallbackCommand { get; }
    public AsyncUiCommand TestCommand { get; }
    public GroupRow(ShellViewModel owner, ProxyGroup model)
    {
        _owner = owner; Model = model; ToggleCommand = new(() => Expanded = !Expanded);
        AutomaticCommand = new(() => owner.SetGroupStrategyAsync(Id, GroupKind.UrlTest));
        FallbackCommand = new(() => owner.SetGroupStrategyAsync(Id, GroupKind.Fallback));
        TestCommand = new(() => owner.TestGroupAsync(Id), () => owner.CanProbe && Model.IsSupported);
    }
    public void Update(ProxyGroup model) { Model = model; ReconcileMembers(); Refresh(); }
    private void ReconcileMembers()
    {
        if (!Expanded) return;
        var previous = _members.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var ids = Model.MemberIds.Concat(Model.SelectedMemberId is { } selected ? [selected, GroupGraph.Direct] : [GroupGraph.Direct]).Distinct(StringComparer.Ordinal);
        var rows = ids.Select(id => previous.TryGetValue(id, out var row) ? row : new GroupMemberRow(_owner, this, id))
            .Where(row => ShowUnavailable || row.Supported || row.Selected).ToArray();
        if (!_members.SequenceEqual(rows)) { _members = rows; Raise(nameof(Members)); }
    }
    public void Refresh()
    {
        foreach (string property in new[] { nameof(Name), nameof(Product), nameof(Retained), nameof(CanRemove), nameof(IsDefault), nameof(Kind), nameof(Detail), nameof(Current), nameof(Latency), nameof(IsTesting), nameof(MemberCount) }) Raise(property);
        foreach (var row in _members) row.Refresh();
        TestCommand.Refresh();
    }
}
public sealed class GroupMemberRow(ShellViewModel owner, GroupRow parent, string id) : Bindable
{
    public string Id { get; } = id;
    public string Name => GroupGraph.Name(Id, owner.Document);
    private ProxyNode? Node => owner.Document.Nodes.FirstOrDefault(node => node.Id == Id);
    private ProxyGroup? Nested => owner.Document.Groups.FirstOrDefault(group => group.Id == Id);
    public bool Selected => parent.Model.Kind == GroupKind.Select ? parent.Model.SelectedMemberId == Id : owner.ResolveGroupMember(parent.Model) == Id;
    public bool Supported => Node?.IsSupported == true || Nested?.IsSupported == true || Id is GroupGraph.Direct or GroupGraph.Reject;
    public string Detail => Id == GroupGraph.Direct ? "DIRECT · 非家宽" : Id == GroupGraph.Reject ? "REJECT" : Nested is { } nested ? "分组 · " + owner.ResolveGroupLabel(nested) : Node?.IsSupported == true ? Node.Protocol.ToString().ToUpperInvariant() : "订阅已移除或不支持";
    public string Latency => Supported ? owner.MemberLatency(Id, parent.Model.TestUrl) : "不可用";
    public string LatencyColor => !Supported || Latency is "超时" or "绑定失败" or "解析失败" or "握手失败" or "无路由" or "拒绝连接" or "内核退出" or "配置无效" or "连接失败" ? "#BF565C" : Latency.EndsWith("ms", StringComparison.Ordinal) ? "#26917C" : "#8A91A0";
    public bool CanSelect => owner.CanEdit && Supported;
    public AsyncUiCommand SelectCommand { get; } = new(() => owner.SelectGroupMemberAsync(parent.Id, id));
    public void Refresh()
    {
        foreach (string property in new[] { nameof(Name), nameof(Detail), nameof(Latency), nameof(LatencyColor), nameof(Selected), nameof(CanSelect) }) Raise(property);
        SelectCommand.Refresh();
    }
}
