using System.Globalization;
using System.Text.RegularExpressions;

namespace EgressController.Policy;

internal static class DomainRuleValidation
{
    internal static bool IsValid(DomainRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Value) || rule.Value.Length > 4096 || rule.Value.Any(char.IsControl)) return false;
        return rule.Kind switch
        {
            DomainMatchKind.Full or DomainMatchKind.Suffix => TryNormalizeName(rule.Value, out _),
            DomainMatchKind.Keyword => !rule.Value.Any(char.IsWhiteSpace) && rule.Value.IndexOfAny(['/', '\\', ':', '@', '*', '?']) < 0,
            DomainMatchKind.Regex => ValidRegex(rule.Value),
            _ => false
        };
    }
    internal static string CompiledValue(DomainRule rule)
        => rule.Kind is DomainMatchKind.Full or DomainMatchKind.Suffix && TryNormalizeName(rule.Value, out var ascii) ? ascii : rule.Value;
    internal static bool TryNormalizeName(string value, out string ascii)
    {
        ascii = "";
        if (value.Length is < 1 or > 253 || value.StartsWith('.') || value.EndsWith('.') || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) || value.IndexOfAny(['/', '\\', ':', '@', '*', '?', '+']) >= 0) return false;
        try { ascii = new IdnMapping().GetAscii(value).ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        if (ascii.Length > 253 || Uri.CheckHostName(ascii) != UriHostNameType.Dns) return false;
        return ascii.Split('.').All(label => label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
    private static bool ValidRegex(string value)
    {
        try { _ = new Regex(value, RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)); return true; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }
}
