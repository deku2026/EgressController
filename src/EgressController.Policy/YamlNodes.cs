using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace EgressController.Policy;

internal static class YamlNodes
{
    internal const int MaximumCharacters = 8 * 1024 * 1024;
    internal static YamlMappingNode Load(string text)
    {
        if (text.Length > MaximumCharacters) throw new FormatException("YAML exceeds size limit.");
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new FormatException("Expected one YAML mapping document.");
        ValidateTree(root, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), 0);
        return root;
    }
    private static void ValidateTree(YamlNode node, HashSet<YamlNode> ancestors, HashSet<YamlNode> validated, int depth)
    {
        if (depth > 40 || !ancestors.Add(node)) throw new FormatException("Recursive or deeply nested YAML.");
        if (validated.Contains(node)) { ancestors.Remove(node); return; }
        if (node is YamlMappingNode map)
        {
            foreach (var (key, value) in map.Children)
            {
                if (key is not YamlScalarNode) throw new FormatException("Mapping keys must be scalars.");
                ValidateTree(value, ancestors, validated, depth + 1);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var item in sequence.Children) ValidateTree(item, ancestors, validated, depth + 1);
        }
        ancestors.Remove(node); validated.Add(node);
    }
    internal static YamlNode? Get(this YamlMappingNode map, string key)
    {
        var visited = new HashSet<YamlMappingNode>(ReferenceEqualityComparer.Instance);
        YamlNode? Find(YamlMappingNode current)
        {
            if (!visited.Add(current)) return null;
            if (current.Children.TryGetValue(new YamlScalarNode(key), out var node)) return node;
            if (key == "<<" || !current.Children.TryGetValue(new YamlScalarNode("<<"), out var merged)) return null;
            if (merged is YamlMappingNode parent) return Find(parent);
            if (merged is YamlSequenceNode parents)
                foreach (var parentMap in parents.Children.OfType<YamlMappingNode>())
                    if (Find(parentMap) is { } found) return found;
            return null;
        }
        return Find(map);
    }
    internal static IEnumerable<string> Keys(this YamlMappingNode map)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<YamlMappingNode>(ReferenceEqualityComparer.Instance);
        void Add(YamlMappingNode current)
        {
            if (!visited.Add(current)) return;
            keys.UnionWith(current.Children.Keys.OfType<YamlScalarNode>().Select(x => x.Value ?? ""));
            if (current.Get("<<") is YamlMappingNode parent) Add(parent);
            else if (current.Get("<<") is YamlSequenceNode parents) foreach (var parentMap in parents.Children.OfType<YamlMappingNode>()) Add(parentMap);
        }
        Add(map);
        return keys;
    }
    internal static string Text(this YamlMappingNode map, string key, string fallback = "") => map.Get(key) is YamlScalarNode scalar ? scalar.Value ?? fallback : fallback;
    internal static int Integer(this YamlMappingNode map, string key, int fallback = 0) => int.TryParse(map.Text(key), CultureInfo.InvariantCulture, out var value) ? value : fallback;
    internal static bool Boolean(this YamlMappingNode map, string key, bool fallback = false) => bool.TryParse(map.Text(key), out var value) ? value : fallback;
    internal static YamlMappingNode Map(this YamlMappingNode map, string key) => map.Get(key) as YamlMappingNode ?? new YamlMappingNode();
    internal static IEnumerable<YamlNode> Items(this YamlMappingNode map, string key) => (map.Get(key) as YamlSequenceNode)?.Children ?? [];
    internal static List<string> Strings(this YamlMappingNode map, string key) => map.Items(key).OfType<YamlScalarNode>().Select(x => x.Value ?? "").ToList();
    internal static YamlSequenceNode Sequence(IEnumerable<string> values) => new(values.Select(x => new YamlScalarNode(x)));
    internal static string Save(YamlMappingNode root)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return writer.ToString();
    }
    internal static bool IsWebUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http") && string.IsNullOrEmpty(uri.UserInfo);
}
