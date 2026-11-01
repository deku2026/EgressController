using EgressController.Policy;
using EgressController.Desktop.Presentation.Controls;
namespace EgressController.Desktop.Preview;

public sealed record CatalogApplication(string Id, string Name, string Product, string Category, string BundlePath, string PrimaryExecutable, IReadOnlyList<string> ExecutablePaths);
public static class PreviewFixtures
{
    public const string SubscriptionUrl = "https://subscription.example.invalid/demo.yaml";
    public static readonly DateTimeOffset ObservationTime = new(2026, 10, 30, 9, 42, 0, TimeSpan.Zero);
    public static readonly IReadOnlyList<CatalogApplication> InstalledApplications =
    [
        App("chatgpt","ChatGPT","openai","AI 助手",3),App("claude","Claude","claude","AI 助手",4),App("cursor","Cursor","cursor","AI 开发",5),App("codex","Codex","openai","AI 开发",3),
        App("chrome","Google Chrome","chrome","浏览器",5),App("vscode","Visual Studio Code","vscode","开发工具",4),App("safari","Safari","safari","浏览器",1),App("notion","Notion","notion","效率工具",3),App("slack","Slack","slack","协作",4),App("spotify","Spotify","spotify","音乐",3),App("discord","Discord","discord","社交",4),App("steam","Steam","steam","游戏",3)
    ];
    private static CatalogApplication App(string id, string name, string product, string category, int count)
    {
        string bundle = $"/Applications/{name}.app", primary = $"{bundle}/Contents/MacOS/{name}";
        var inventory = new List<string> { primary };
        for (int i = 1; i < count; i++) inventory.Add($"{bundle}/Contents/Frameworks/{name} Helper{(i == 1 ? "" : $" ({(i == 2 ? "Renderer" : i == 3 ? "GPU" : "Plugin")})")}.app/Contents/MacOS/{name} Helper{(i == 1 ? "" : $" ({(i == 2 ? "Renderer" : i == 3 ? "GPU" : "Plugin")})")}");
        return new(id, name, product, category, bundle, primary, inventory);
    }
    public static ManagedApplication Manage(CatalogApplication app, ExitChoice via) => new() { Id = "app-" + app.Id, Name = app.Name, BundlePath = app.BundlePath, BundleIdentifier = "example.preview." + app.Id, PrimaryExecutable = app.PrimaryExecutable, ExecutablePaths = app.ExecutablePaths.ToList(), Via = via };
    public static MacPolicyDocument CreateDocument()
    {
        var doc = new MacPolicyDocument { Interfaces = new() { HomeId = "fixture-ethernet", NonHomeId = "fixture-wifi" } };
        var imported = ClashSubscriptionImporter.Import(SubscriptionYaml, "fixture-subscription"); doc.Nodes = imported.Nodes.ToList(); doc.Groups = imported.Groups.ToList();
        // This populated workspace represents an explicit previously saved user choice, not an importer default.
        doc.Groups.Single(group => group.Name == "手动选择").SelectedMemberId = doc.Nodes.Single(node => node.Name == "东京 · 01").Id;
        doc.Subscriptions.Add(new() { Id = "fixture-subscription", Name = "我的示例订阅", Url = SubscriptionUrl });
        doc.Defaults.Global = ExitChoice.Proxy(doc.Groups.First(x => x.Kind == GroupKind.UrlTest).Id);
        foreach (var (id, via) in new[] { ("chatgpt", ExitChoice.Home), ("claude", ExitChoice.Proxy(doc.Defaults.Global.TargetId!)), ("cursor", ExitChoice.Proxy(doc.Defaults.Global.TargetId!)), ("chrome", ExitChoice.NonHome), ("notion", ExitChoice.NonHome), ("spotify", ExitChoice.NonHome) }) doc.Applications.Add(Manage(InstalledApplications.First(x => x.Id == id), via));
        doc.RuleSets = [Rule("openai", "OpenAI", ExitChoice.Proxy(doc.Defaults.Global.TargetId!)), Rule("google", "Google", ExitChoice.NonHome), Rule("github", "GitHub", ExitChoice.Home)];
        return doc;
    }
    public static ManagedRuleSet Rule(string id, string name, ExitChoice via) => new()
    {
        Id = "rule-" + id,
        Name = name,
        Via = via,
        Sources = id is "google" or "youtube" ? ["geosite/" + id + ".yaml", "geoip/" + id + ".yaml"] : ["geosite/" + id + ".yaml"],
        Domains = id switch { "openai" => [new(DomainMatchKind.Suffix, "openai.com"), new(DomainMatchKind.Suffix, "chatgpt.com"), new(DomainMatchKind.Suffix, "oaistatic.com"), new(DomainMatchKind.Suffix, "oaiusercontent.com")], "google" => [new(DomainMatchKind.Suffix, "google.com"), new(DomainMatchKind.Suffix, "googleapis.com"), new(DomainMatchKind.Suffix, "gstatic.com")], "github" => [new(DomainMatchKind.Suffix, "github.com"), new(DomainMatchKind.Suffix, "githubusercontent.com")], _ => [new(DomainMatchKind.Suffix, id + ".com")] },
        IpCidrs = id == "google" ? ["192.0.2.0/24", "2001:db8:1::/48"] : id == "youtube" ? ["203.0.113.0/24"] : []
    };
    public static IReadOnlyList<Observation> Observations => Enumerable.Range(0, 21).Select(i => new Observation(ObservationTime.AddSeconds((i - 20) * 30), i == 9 ? null : new double[] { .7, 1.1, 2.5, 1.8, 3.1, 2.4, 1.3, 3.7, 4.4, 0, 2.8, 5.5, 4.1, 6.2, 4.8, 5.1, 3.4, 4.1, 2.7, 3.4, 2.4 }[i], i == 9 ? null : .15 + (i % 6) * .09)).ToArray();
    // Semantically validated synthetic raw-YAML resources, not a live MetaCubeX download.
    // IP fixtures are documentation networks; they do not represent the actual provider ranges.
    // Unknown DSL sources never become prepared just by being named.
    public static readonly IReadOnlyDictionary<string, string> RuleSourceYaml = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["geosite/openai.yaml"] = "payload: ['+.openai.com', '+.chatgpt.com', '+.oaistatic.com', '+.oaiusercontent.com']",
        ["geosite/google.yaml"] = "payload: ['+.google.com', '+.googleapis.com', '+.gstatic.com']",
        ["geoip/google.yaml"] = "payload: ['192.0.2.0/24', '2001:db8:1::/48']",
        ["geosite/github.yaml"] = "payload: ['+.github.com', '+.githubusercontent.com']",
        ["geosite/youtube.yaml"] = "payload: ['+.youtube.com']",
        ["geoip/youtube.yaml"] = "payload: ['203.0.113.0/24']",
        ["geosite/netflix.yaml"] = "payload: ['+.netflix.com']",
        ["geosite/apple.yaml"] = "payload: ['+.apple.com']"
    };
    public const string SubscriptionYaml = """
proxies:
  - name: 东京 · 01
    type: vless
    server: tokyo.example.invalid
    port: 443
    uuid: 11111111-1111-4111-8111-111111111111
    tls: true
    servername: tokyo.example.invalid
    network: tcp
  - name: 新加坡 · 02
    type: vless
    server: singapore.example.invalid
    port: 443
    uuid: 22222222-2222-4222-8222-222222222222
    tls: true
    servername: singapore.example.invalid
    network: tcp
  - name: 洛杉矶 · 03
    type: trojan
    server: losangeles.example.invalid
    port: 443
    password: fixture-not-a-real-secret
    sni: losangeles.example.invalid
  - name: 香港 · Hysteria2
    type: hysteria2
    server: unsupported.example.invalid
    port: 443
    password: fixture-only
proxy-groups:
  - name: 全球优选
    type: url-test
    proxies: [东京 · 01, 新加坡 · 02, 洛杉矶 · 03]
    url: https://probe.example.invalid/generate_204
    interval: 300
    tolerance: 30
  - name: 手动选择
    type: select
    proxies: [东京 · 01, 新加坡 · 02, 洛杉矶 · 03]
  - {name: Claude, type: select, proxies: [全球优选, 东京 · 01, 新加坡 · 02, 洛杉矶 · 03, DIRECT]}
  - {name: OpenAI, type: select, proxies: [全球优选, 东京 · 01, 新加坡 · 02, 洛杉矶 · 03, DIRECT]}
  - {name: GitHub, type: select, proxies: [全球优选, 东京 · 01, 新加坡 · 02, 洛杉矶 · 03, DIRECT]}
  - {name: Spotify, type: select, proxies: [DIRECT, 全球优选, 东京 · 01]}
  - {name: YouTube, type: select, proxies: [全球优选, 东京 · 01, 新加坡 · 02, 洛杉矶 · 03, DIRECT]}
  - {name: Google, type: select, proxies: [全球优选, 新加坡 · 02, DIRECT]}
  - {name: Apple, type: select, proxies: [DIRECT, 全球优选, 东京 · 01]}
""";
}
