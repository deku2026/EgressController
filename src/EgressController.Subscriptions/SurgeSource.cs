namespace EgressController.Subscriptions;

/// <summary>Conservative structural gate for raw Surge subscriptions; it does not claim native syntax validation.</summary>
public sealed record SurgeSource(string ProxySection, string[] Names)
{
    public static SurgeSource Parse(string content)
    {
        bool proxy = false; var definitions = new List<string>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[')) { if (!line.EndsWith(']')) throw new InvalidDataException("Surge 订阅章节格式无效。"); proxy = line == "[Proxy]"; continue; }
            if (line.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不能将私人进程分流配置当作机场订阅。");
            if (!proxy) continue;
            int equals = line.IndexOf('='); if (equals < 1) throw new InvalidDataException("Surge 订阅节点格式无效。");
            string name = line[..equals].Trim(); var parts = line[(equals + 1)..].Split(',', StringSplitOptions.TrimEntries);
            if (parts[0] is "direct" or "reject") continue;
            if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is ',' or '#' or ';' or '=') || name.StartsWith("EC-", StringComparison.Ordinal) || !names.Add(name) ||
                parts.Length < 3 || string.IsNullOrWhiteSpace(parts[1]) || !int.TryParse(parts[2], out int port) || port is < 1 or > 65535)
                throw new InvalidDataException("Surge 订阅节点为空、重复或格式无效。");
            definitions.Add(line);
        }
        if (names.Count == 0) throw new InvalidDataException("Surge 订阅没有可用的内联节点。");
        return new(string.Join('\n', definitions), names.ToArray());
    }
}
