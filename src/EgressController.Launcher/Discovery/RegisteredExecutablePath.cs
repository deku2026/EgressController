namespace EgressController.Launcher.Discovery;

/// <summary>Registry file paths are not command lines. Spaces belong to the filename.</summary>
public static class RegisteredExecutablePath
{
    public static string? Parse(string? value, bool icon = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();
        if (text.StartsWith('@')) return null;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end < 0) return null;
            string suffix = text[(end + 1)..].Trim();
            if (suffix.Length > 0 && (!icon || !IsIconIndex(suffix))) return null;
            text = text[1..end];
        }
        else if (icon)
        {
            int comma = text.LastIndexOf(',');
            if (comma >= 0 && IsIconIndex(text[comma..])) text = text[..comma].TrimEnd();
        }
        text = Environment.ExpandEnvironmentVariables(text);
        try { return Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : null; }
        catch (ArgumentException) { return null; }
    }

    private static bool IsIconIndex(string suffix)
        => suffix.StartsWith(',') && int.TryParse(suffix.AsSpan(1).Trim(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);
}
