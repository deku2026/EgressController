namespace EgressController.Desktop.Presentation.Controls;

/// <summary>Subscription icon hints select trusted bundled artwork; URLs are never fetched by views.</summary>
public static class GroupArtwork
{
    public static string Product(string name, string icon)
    {
        string hint = name.ToLowerInvariant();
        if (Uri.TryCreate(icon, UriKind.Absolute, out var uri)) hint += " " + Path.GetFileNameWithoutExtension(uri.AbsolutePath).ToLowerInvariant();
        foreach (string product in new[] { "claude", "gemini", "openai", "github", "spotify", "youtube", "google", "apple", "netflix", "telegram", "notion", "slack", "codex" })
            if (hint.Contains(product, StringComparison.Ordinal)) return product;
        return hint.Contains("gpt", StringComparison.Ordinal) ? "openai" : "domain";
    }
}
