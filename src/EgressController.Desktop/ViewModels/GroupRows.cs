using EgressController.Policy;
using EgressController.Desktop.Presentation.Controls;
namespace EgressController.Desktop.ViewModels;

public sealed class GroupRow : Bindable
{
    private readonly ShellViewModel _owner;
    private bool _expanded;
    public ProxyGroup Model { get; private set; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Product => GroupArtwork.Product(Model.Name, Model.Icon);
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) { Raise(nameof(Chevron)); Raise(nameof(Members)); } } }
    public string Chevron => Expanded ? "chevron-up" : "chevron-down";
    public bool IsDefault => _owner.Document.Defaults.Global.TargetId == Id;
    public bool CanRemove => !IsDefault && !_owner.Document.Applications.Any(app => app.Via.TargetId == Id) && !_owner.Document.RuleSets.Any(rule => rule.Via.TargetId == Id);
    public bool Retained { get => Model.Retained; set { if (value == Model.Retained || !value && !CanRemove) return; Model.Retained = value; _owner.GroupRetentionChanged(); Raise(); } }
    public string Kind => Model.Kind switch { GroupKind.Select => "手动选择", GroupKind.UrlTest => "自动优选", GroupKind.Fallback => "故障转移", _ => "不支持" };
    public string Detail => !Model.IsSupported ? string.Join(" · ", Model.Diagnostics.Select(item => item.Message)) : $"{Model.MemberIds.Count} 个成员 · {(Model.Kind == GroupKind.Select ? "选择后即时生效" : $"每 {Model.IntervalSeconds} 秒检查")} · {(_owner.IsPreview ? "示例观测" : "非家宽出口")}";
    public string Current => _owner.ResolveGroupLabel(Model);
    public string Latency => _owner.GroupLatency(Model);
    public IReadOnlyList<GroupMemberRow> Members => Expanded ? Model.MemberIds.Concat([GroupGraph.Direct]).Distinct(StringComparer.Ordinal).Select(id => new GroupMemberRow(_owner, this, id)).ToArray() : [];
    public UiCommand ToggleCommand { get; }
    public AsyncUiCommand AutomaticCommand { get; }
    public AsyncUiCommand FallbackCommand { get; }
    public AsyncUiCommand TestCommand { get; }
    public GroupRow(ShellViewModel owner, ProxyGroup model)
    {
        _owner = owner; Model = model; ToggleCommand = new(() => Expanded = !Expanded);
        AutomaticCommand = new(() => owner.SetGroupStrategyAsync(Id, GroupKind.UrlTest));
        FallbackCommand = new(() => owner.SetGroupStrategyAsync(Id, GroupKind.Fallback));
        TestCommand = new(() => owner.TestGroupAsync(Id), () => owner.CanEdit && Model.IsSupported);
    }
    public void Update(ProxyGroup model) { Model = model; Refresh(); }
    public void Refresh()
    {
        foreach (string property in new[] { nameof(Name), nameof(Product), nameof(Retained), nameof(CanRemove), nameof(IsDefault), nameof(Kind), nameof(Detail), nameof(Current), nameof(Latency), nameof(Members) }) Raise(property);
        TestCommand.Refresh();
    }
}
public sealed class GroupMemberRow
{
    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Latency { get; }
    public bool Selected { get; }
    public bool CanSelect { get; }
    public AsyncUiCommand SelectCommand { get; }
    public GroupMemberRow(ShellViewModel owner, GroupRow parent, string id)
    {
        Id = id; Name = GroupGraph.Name(id, owner.Document);
        var node = owner.Document.Nodes.FirstOrDefault(node => node.Id == id); var nested = owner.Document.Groups.FirstOrDefault(group => group.Id == id);
        Selected = parent.Model.Kind == GroupKind.Select ? parent.Model.SelectedMemberId == id : owner.ResolveGroupMember(parent.Model) == id;
        Detail = id == GroupGraph.Direct ? "绑定非家宽 · 可手动选择" : id == GroupGraph.Reject ? "拒绝连接" : nested is not null ? "分组 · " + owner.ResolveGroupLabel(nested) : node?.IsSupported == true ? node.Protocol.ToString().ToUpperInvariant() : "不可用 · 请检查订阅";
        Latency = owner.MemberLatency(id, parent.Model.TestUrl);
        CanSelect = owner.CanEdit && (node?.IsSupported == true || nested?.IsSupported == true || id is GroupGraph.Direct or GroupGraph.Reject);
        SelectCommand = new(() => owner.SelectGroupMemberAsync(parent.Id, id), () => CanSelect);
    }
}
