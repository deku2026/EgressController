using EgressController.Core.Models;

namespace EgressController.Launcher.Discovery;

public sealed record SupportedApplication(string Id, string Name, string[] ExecutableStems, string[] DisplayNames);

/// <summary>Classify the primary executable, never a nested EXE or arbitrary substring.</summary>
public static class SupportedApplicationCatalog
{
    public static IReadOnlyList<SupportedApplication> Applications { get; } = [
        new("claude", "Claude", ["claude"], ["Claude"]),
        new("chatgpt", "ChatGPT", ["chatgpt"], ["ChatGPT"]),
        new("codex", "Codex", ["codex"], ["Codex"]),
        new("cursor", "Cursor", ["cursor"], ["Cursor"]),
        new("windsurf", "Windsurf", ["windsurf"], ["Windsurf"]),
        new("gemini", "Gemini", ["gemini"], ["Gemini"]),
        new("copilot", "Copilot", ["copilot", "microsoft.copilot"], ["Copilot", "Microsoft Copilot", "Microsoft 365 Copilot"]),
        new("perplexity", "Perplexity", ["perplexity"], ["Perplexity"]),
        new("ollama", "Ollama", ["ollama", "ollama app"], ["Ollama"]),
        new("lmstudio", "LM Studio", ["lmstudio", "lm studio"], ["LM Studio"]),
        new("jan", "Jan", ["jan"], ["Jan"]),
        new("anythingllm", "AnythingLLM", ["anythingllm"], ["AnythingLLM"]),
        new("cherrystudio", "Cherry Studio", ["cherrystudio", "cherry studio"], ["Cherry Studio"]),
        new("trae", "Trae", ["trae"], ["Trae"]),
        new("kiro", "Kiro", ["kiro"], ["Kiro"]),
        new("void", "Void", ["void"], ["Void"]),
        new("opencode", "OpenCode", ["opencode"], ["OpenCode"]),
        new("chrome", "Google Chrome", ["chrome"], ["Google Chrome", "Google Chrome Beta", "Google Chrome Dev", "Google Chrome Canary"]),
        new("edge", "Microsoft Edge", ["msedge", "edge"], ["Microsoft Edge", "Microsoft Edge Beta", "Microsoft Edge Dev", "Microsoft Edge Canary"]),
        new("firefox", "Firefox", ["firefox"], ["Firefox", "Mozilla Firefox", "Firefox Developer Edition", "Firefox Nightly"]),
        new("brave", "Brave", ["brave"], ["Brave"]),
        new("opera", "Opera", ["opera", "opera_gx"], ["Opera", "Opera GX"]),
        new("vivaldi", "Vivaldi", ["vivaldi"], ["Vivaldi"]),
        new("arc", "Arc", ["arc"], ["Arc"]),
        new("zen", "Zen", ["zen"], ["Zen", "Zen Browser"]),
        new("chromium", "Chromium", ["chromium"], ["Chromium"]),
        new("waterfox", "Waterfox", ["waterfox"], ["Waterfox"]),
        new("librewolf", "LibreWolf", ["librewolf"], ["LibreWolf"]),
        new("yandex", "Yandex Browser", ["yandex"], ["Yandex Browser"]),
        new("qqbrowser", "QQ 浏览器", ["qqbrowser"], ["QQ浏览器", "QQ Browser"]),
        new("360chrome", "360 浏览器", ["360chrome", "360se"], ["360浏览器", "360安全浏览器", "360极速浏览器"]),
        new("thorium", "Thorium", ["thorium"], ["Thorium"]),
        new("floorp", "Floorp", ["floorp"], ["Floorp"]),
        new("duckduckgo", "DuckDuckGo", ["duckduckgo"], ["DuckDuckGo"]),
    ];

    public static SupportedApplication? ForExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string stem = Path.GetFileNameWithoutExtension(path.Trim());
        return Applications.FirstOrDefault(item => item.ExecutableStems.Contains(stem, StringComparer.OrdinalIgnoreCase));
    }
    public static SupportedApplication? ForName(string? name) => Applications.FirstOrDefault(item =>
        item.DisplayNames.Contains(name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase));
    public static SupportedApplication? Identify(LaunchTarget target)
    {
        if (target.Kind is not (LaunchKind.DirectExe or LaunchKind.PackagedAumid or LaunchKind.CliNative)) return null;
        string? path = target.CanonicalExecutable ?? target.Command;
        return ForExecutable(path) ?? (string.IsNullOrEmpty(path) ? ForName(target.Name) : null);
    }
    public static bool IsSupported(LaunchTarget target) => Identify(target) is not null;
    public static bool IsSupportedProcessStem(string value) => ForExecutable(value) is not null;
}
