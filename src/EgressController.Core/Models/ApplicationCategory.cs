namespace EgressController.Core.Models;

/// <summary>Presentation metadata only. Never infer category, routing or protection from a product.</summary>
public sealed record ApplicationCategory(string Id, string Name, string Icon)
{
    public static IReadOnlyList<ApplicationCategory> BuiltIn { get; } = [
        new("", "未分类", "uncategorized"), new("ai", "AI", "ai"),
        new("browser", "浏览器", "browser"), new("games", "游戏", "games"),
        new("social", "社交", "social"), new("media", "影音", "media"),
        new("office", "办公", "office"), new("development", "开发工具", "development"),
        new("other", "其他", "folder")
    ];
    public static bool IsValid(string? id) => BuiltIn.Any(category => category.Id == (id ?? ""));
    public override string ToString() => Name;
}
