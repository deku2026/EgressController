using EgressController.Policy;
using EgressController.Desktop.Preview;

namespace EgressController.Desktop.ViewModels;

public abstract class RouteRow(Func<string> global, Func<IReadOnlyList<Choice>> choices, Action changed) : Bindable
{
    protected abstract ExitChoice Exit { get; set; }
    public int RouteIndex
    {
        get => (int)Exit.Kind;
        set
        {
            if (RouteIndex == value) return;
            Exit = value == 0 ? ExitChoice.Home : value == 1 ? ExitChoice.NonHome : ExitChoice.Proxy(global());
            NotifyRoute(); changed();
        }
    }
    public bool ProxyVisible => RouteIndex == 2;
    public IReadOnlyList<Choice> ProxyChoices => choices();
    public Choice? SelectedProxyTarget
    {
        get => ProxyChoices.FirstOrDefault(x => x.Id == Exit.TargetId);
        set
        {
            if (value is null || Exit.Kind == ExitKind.Proxy && Exit.TargetId == value.Id) return;
            Exit = ExitChoice.Proxy(value.Id); NotifyRoute(); changed();
        }
    }
    public string RouteDescription => Exit.Kind switch { ExitKind.Home => "家宽直连", ExitKind.NonHome => "非家宽直连", _ => "节点 / 分组" };
    protected void NotifyRoute()
    {
        Raise(nameof(RouteIndex)); Raise(nameof(ProxyVisible)); Raise(nameof(SelectedProxyTarget)); Raise(nameof(ProxyChoices)); Raise(nameof(RouteDescription));
    }
}

public sealed class AppRow : RouteRow
{
    public ManagedApplication Model { get; private set; }
    protected override ExitChoice Exit { get => Model.Via; set => Model.Via = value; }
    public string Name => Model.Name;
    public string Product => PreviewFixtures.InstalledApplications.FirstOrDefault(x => x.BundlePath == Model.BundlePath)?.Id is "codex" ? "codex" : PreviewFixtures.InstalledApplications.FirstOrDefault(x => x.BundlePath == Model.BundlePath)?.Product ?? "app";
    public string Category => PreviewFixtures.InstalledApplications.FirstOrDefault(x => x.BundlePath == Model.BundlePath)?.Category ?? "已管理应用";
    public string Subtitle => $"{Category} · {Model.ExecutablePaths.Count} 个固定可执行文件";
    public string BundlePath => Model.BundlePath;
    public IReadOnlyList<string> ExecutablePaths => Model.ExecutablePaths;
    public UiCommand RemoveCommand { get; }
    public AppRow(ManagedApplication model, Func<string> global, Func<IReadOnlyList<Choice>> choices, Action changed, Action<AppRow> remove) : base(global, choices, changed)
    {
        Model = model; RemoveCommand = new(() => remove(this));
    }
    public bool UpdateModel(ManagedApplication model)
    {
        bool inventoryChanged = !Model.ExecutablePaths.SequenceEqual(model.ExecutablePaths, StringComparer.Ordinal);
        Model = model;
        Raise(nameof(Name)); Raise(nameof(Product)); Raise(nameof(Category)); Raise(nameof(BundlePath)); NotifyRoute();
        if (inventoryChanged) NotifyInventory();
        return inventoryChanged;
    }
    public void NotifyInventory() { Raise(nameof(Subtitle)); Raise(nameof(ExecutablePaths)); }
}

public sealed class CatalogRow : Bindable
{
    private bool _selected;
    private readonly Action _changed;
    public CatalogApplication Model { get; }
    public string Name => Model.Name;
    public string Product => Model.Id == "codex" ? "codex" : Model.Product;
    public string Subtitle => $"{Model.Category} · {Model.BundlePath}";
    public bool IsManaged { get; }
    public bool CanSelect => !IsManaged;
    public bool IsSelected { get => _selected; set { if (!IsManaged && Set(ref _selected, value)) _changed(); } }
    public CatalogRow(CatalogApplication model, Action changed, bool isManaged = false) { Model = model; _changed = changed; IsManaged = isManaged; }
}

public sealed record RuleContent(IReadOnlyList<DomainRule> Domains, IReadOnlyList<string> Cidrs);

public sealed class RuleRow : RouteRow
{
    private readonly Func<ManagedRuleSet, RuleContent> _content;
    public ManagedRuleSet Model { get; private set; }
    protected override ExitChoice Exit { get => Model.Via; set => Model.Via = value; }
    public string Name => Model.Name;
    public string Product => Model.Id.StartsWith("rule-", StringComparison.Ordinal) ? Model.Id[5..] : Model.IpCidrs.Count > 0 ? "custom" : "domain";
    public string Subtitle => $"{_content(Model).Domains.Count} 条域名" + (_content(Model).Cidrs.Count > 0 ? $" + {_content(Model).Cidrs.Count} 条 IP/CIDR" : "") + (Model.Sources.Count > 0 ? " · MetaCubeX" : " · 自定义");
    public string MatchSummary => string.Join("\n", _content(Model).Domains.Select(x => $"{x.Kind.ToString().ToLowerInvariant()}: {x.Value}").Concat(_content(Model).Cidrs.Select(x => "cidr: " + x)));
    public UiCommand RemoveCommand { get; }
    public UiCommand MoveUpCommand { get; }
    public UiCommand MoveDownCommand { get; }
    public RuleRow(ManagedRuleSet model, Func<string> global, Func<IReadOnlyList<Choice>> choices, Action changed, Action<RuleRow> remove, Action<RuleRow, int> move, Func<RuleRow, int, bool> canMove, Func<ManagedRuleSet, RuleContent> content) : base(global, choices, changed)
    {
        Model = model; _content = content; RemoveCommand = new(() => remove(this));
        MoveUpCommand = new(() => move(this, -1), () => canMove(this, -1)); MoveDownCommand = new(() => move(this, 1), () => canMove(this, 1));
    }
    public void UpdateModel(ManagedRuleSet model)
    {
        Model = model; Raise(nameof(Name)); Raise(nameof(Product)); Raise(nameof(Subtitle)); Raise(nameof(MatchSummary)); NotifyRoute(); MoveUpCommand.Refresh(); MoveDownCommand.Refresh();
    }
}

public sealed class RuleSuggestion
{
    public string Id { get; }
    public string Name { get; }
    public string Product => Id;
    public string Detail { get; }
    public bool IsAdded { get; }
    public string AddLabel => IsAdded ? "已添加" : "添加集合";
    public UiCommand AddCommand { get; }
    public RuleSuggestion(string id, string name, string detail, Action<string, string> add, bool isAdded = false)
    {
        Id = id; Name = name; Detail = detail; IsAdded = isAdded; AddCommand = new(() => add(id, name), () => !IsAdded);
    }
}

public sealed class NodeRow : Bindable
{
    public ProxyNode Model { get; }
    private readonly Action _changed;
    private readonly Func<ProxyGroup?> _group;
    private double? _latency;
    public string Name => Model.Name;
    public string Protocol => Model.Protocol.ToString().ToUpperInvariant();
    public string Subtitle => Model.IsSupported ? $"{Protocol}{(Model.Tls ? " · TLS" : "")} · 非家宽网卡" : string.Join(" · ", Model.Diagnostics.Select(x => x.Message));
    public string Initial => Name.StartsWith("东京", StringComparison.Ordinal) ? "JP" : Name.StartsWith("新加坡", StringComparison.Ordinal) ? "SG" : Name.StartsWith("洛杉矶", StringComparison.Ordinal) ? "US" : Name.StartsWith("香港", StringComparison.Ordinal) ? "HK" : "—";
    public bool IsSupported => Model.IsSupported;
    public bool IsUnsupported => !IsSupported;
    public bool IsMember
    {
        get => _group()?.MemberIds.Contains(Model.Id, StringComparer.Ordinal) ?? false;
        set
        {
            var group = _group(); if (group is null || !IsSupported) return;
            if (value && !group.MemberIds.Contains(Model.Id, StringComparer.Ordinal)) group.MemberIds.Add(Model.Id);
            else if (!value) group.MemberIds.Remove(Model.Id);
            Raise(); _changed();
        }
    }
    public string Latency => _latency is { } value ? $"{value:0} ms" : "—";
    public string Status => Model.Diagnostics.Any(diagnostic => diagnostic.Code == "subscription.removed") ? "已移除" : IsSupported ? "可用协议" : "不支持";
    public NodeRow(ProxyNode model, Func<ProxyGroup?> group, Action changed) { Model = model; _group = group; _changed = changed; }
    public void SetLatency(double? value) { _latency = value; Raise(nameof(Latency)); }
    public void RefreshMember() => Raise(nameof(IsMember));
}
public sealed record EventRow(string Time, string Title, string Detail, string Kind);
