using EgressController.Core.Models;

namespace EgressController.Launcher.Discovery;

/// <summary>
/// The product catalog is intentionally small and explicit. Discovery still inspects the
/// normal Windows package/registry/Program Files sources, but only AI clients and browsers are
/// exposed to the user. Once an entry is selected, every recursively discovered EXE below its
/// ownership root is sent to the process-name compiler.
/// </summary>
public static class SupportedApplicationCatalog
{
    private static readonly string[] SupportedProcessStems =
    [
        // AI desktop clients and local-model tools.
        "claude", "chatgpt", "cursor", "windsurf", "codex", "gemini", "copilot",
        "perplexity", "ollama", "lmstudio", "jan", "anythingllm", "cherrystudio",
        "cline", "continue", "void", "trae", "lobehub", "kiro",

        // Windows browsers and Chromium-family derivatives.
        "chrome", "msedge", "edge", "firefox", "brave", "opera", "opera_gx", "vivaldi",
        "arc", "zen", "chromium", "waterfox", "librewolf", "yandex", "qqbrowser",
        "360chrome", "iexplore", "thorium", "floorp", "sidekick", "maxthon", "duckduckgo",
    ];

    private static readonly string[] SupportedNameHints =
    [
        "claude", "chatgpt", "cursor", "windsurf", "codex", "gemini", "copilot",
        "perplexity", "ollama", "lm studio", "anythingllm", "cherry studio", "cline",
        "continue", "trae", "lobehub", "browser", "chrome", "edge", "firefox", "brave",
        "opera", "vivaldi", "arc", "chromium", "waterfox", "librewolf", "yandex",
        "qq browser", "360浏览器", "thorium", "floorp", "sidekick", "maxthon",
    ];

    public static bool IsSupported(LaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is not (LaunchKind.DirectExe or LaunchKind.PackagedAumid))
            return false;

        if (EnumerateExecutableStems(target).Any(IsSupportedProcessStem))
            return true;

        string descriptiveText = string.Join(
            ' ',
            target.Name,
            target.PackageFamily,
            target.Aumid,
            target.Source);
        return SupportedNameHints.Any(hint => descriptiveText.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSupportedProcessStem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string stem = Path.GetFileNameWithoutExtension(value.Trim());
        return SupportedProcessStems.Any(candidate =>
            string.Equals(stem, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateExecutableStems(LaunchTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.CanonicalExecutable))
            yield return target.CanonicalExecutable;
        if (!string.IsNullOrWhiteSpace(target.Command))
            yield return target.Command;
        foreach (string executable in target.OwnedExecutables)
            yield return executable;
    }
}
