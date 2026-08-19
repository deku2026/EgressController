using EgressController.Rules.Parsing;

namespace EgressController.Rules.Tests;

/// <summary>
/// Step 05 Gate: scan the planning-day <c>geo/geosite/*.list</c> corpus and require that every
/// line is either supported or explicitly listed. UnknownLineCount &gt; 0 while the Gate is green
/// is forbidden, so this test fails the whole step if any file has an unsupported line.
/// The corpus is an optional local oracle clone selected through EGRESS_RULES_ROOT (no network).
/// </summary>
public class CorpusCensusTests
{
    [Fact]
    public void Every_line_in_the_corpus_is_supported_by_the_strict_parser()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("EGRESS_RULES_ROOT");
        string? oracleDir = ResolveGeositeDirectory(configuredRoot);
        if (oracleDir is null)
        {
            // No oracle on this machine; this mandatory gate must be run with EGRESS_RULES_ROOT.
            Assert.Skip("oracle corpus not found; set EGRESS_RULES_ROOT to meta-rules-dat or geo\\geosite.");
        }

        string[] files = Directory.GetFiles(oracleDir!, "*.list");
        Assert.True(files.Length > 0, $"no .list files found under {oracleDir}");

        long totalLines = 0;
        var failures = new List<string>();

        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            totalLines += lines.Length;

            if (!StrictDomainListParser.TryParse(lines, Path.GetFileName(file), out var rules, out var failure))
            {
                failures.Add($"{Path.GetFileName(file)} line {failure!.LineNumber}: '{failure.LineText}' — {failure.Reason}");
            }
            else
            {
                // (optional) transitively assert every compiled rule is valid
                Assert.NotNull(rules);
            }
        }

        Assert.True(failures.Count == 0,
            $"unsupported syntax in corpus ({failures.Count} file(s), {totalLines} lines total):{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }

    private static string? ResolveGeositeDirectory(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            return null;

        string full = Path.GetFullPath(configuredRoot.Trim().Trim('"'));
        if (!Directory.Exists(full))
            return null;
        if (HasListFiles(full))
            return full;

        string nested = Path.Combine(full, "geo", "geosite");
        if (HasListFiles(nested))
            return nested;

        if (string.Equals(Path.GetFileName(full), "geo", StringComparison.OrdinalIgnoreCase))
        {
            nested = Path.Combine(full, "geosite");
            if (HasListFiles(nested))
                return nested;
        }

        return null;
    }

    private static bool HasListFiles(string directory)
        => Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.list").Any();
}
