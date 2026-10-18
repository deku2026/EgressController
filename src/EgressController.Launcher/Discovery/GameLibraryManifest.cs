using System.Text;

namespace EgressController.Launcher.Discovery;

/// <summary>Read-only Valve KeyValues metadata; no game launches, drive scan or external parser runtime.</summary>
public static class GameLibraryManifest
{
    public static IReadOnlyList<string> SteamLibraries(string steamRoot, string text)
        => Values(text, "path").Prepend(steamRoot).Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static InstalledApplication? SteamGame(string library, string text)
    {
        string? id = Values(text, "appid").FirstOrDefault(), directory = Values(text, "installdir").FirstOrDefault();
        if (!uint.TryParse(id, out _) || string.IsNullOrWhiteSpace(directory) || Path.IsPathFullyQualified(directory)) return null;
        string common = Path.GetFullPath(Path.Combine(library, "steamapps", "common"));
        string root = Path.GetFullPath(Path.Combine(common, directory));
        if (!root.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return new("steam:" + id, Values(text, "name").FirstOrDefault() ?? directory, root, null, null, null, "Steam 游戏库");
    }

    private static IEnumerable<string> Values(string text, string requested)
    {
        var tokens = Tokens(text).ToArray();
        for (int index = 0; index + 1 < tokens.Length; index++)
            if (tokens[index] == requested && tokens[index + 1] is not ("{" or "}")) yield return tokens[++index];
    }
    private static IEnumerable<string> Tokens(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (text[i] is '{' or '}') { yield return text[i].ToString(); continue; }
            if (text[i] != '"') continue;
            var value = new StringBuilder();
            while (++i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '"') i++;
                value.Append(text[i]);
            }
            yield return value.ToString();
        }
    }
}
