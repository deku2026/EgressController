namespace EgressController.Core.Models;

/// <summary>
/// How a <see cref="LaunchTarget"/> is canonicalized / keyed for discovery dedup (plan §1.5/§10).
/// </summary>
public enum LaunchKind
{
    DirectExe = 0,
    PackagedAumid = 1,
    Shortcut = 2,
    CliNative = 3,
    CliWrapperResolved = 4,
}

/// <summary>
/// A discoverable / launchable app or command (plan §6 / §Step 10). The dedup key is
/// <see cref="DiscoveryKey"/>, not any single path: DirectExe/CliNative key on the canonical final
/// exe path; PackagedAumid on packageFamily+AUMID; Shortcut keeps its args/working dir and is only
/// merged when it truly resolves to another key's executable.
/// </summary>
public sealed class LaunchTarget
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public LaunchKind Kind { get; init; } = LaunchKind.DirectExe;

    /// <summary>Executable path (DirectExe/CliNative), or the command name (CLI wrapper).</summary>
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>PackageFamily/AUMID for packaged apps.</summary>
    public string? PackageFamily { get; init; }
    public string? Aumid { get; init; }

    /// <summary>Canonical final exe path when known (used for ownership, §1.5).</summary>
    public string? CanonicalExecutable { get; init; }

    public IReadOnlyList<string> OwnedRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OwnedExecutables { get; init; } = Array.Empty<string>();
    /// <summary>User-selected launch/routing toggle. Newly discovered targets start unchecked.</summary>
    public bool Managed { get; set; }

    /// <summary>Optional local icon path; the UI falls back to a kind-specific glyph.</summary>
    public string? IconPath { get; init; }

    /// <summary>Discovery provider metadata shown in the catalog.</summary>
    public string? Source { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }

    /// <summary>Stable key for merging discovery providers (see class doc). Purely lexical — no cwd/
    /// I/O dependence so it is unit-testable and AOT-safe.</summary>
    public string DiscoveryKey => Kind switch
    {
        LaunchKind.PackagedAumid => $"pkg:{PackageFamily ?? Aumid ?? Id}:{Aumid ?? Id}",
        LaunchKind.Shortcut => Prefix("sc:", Command, Id),
        _ => $"exe:{Prefix("", CanonicalExecutable, Command)}",
    };

    public bool ResolutionUnsupported { get; init; }

    /// <summary>
    /// Whether the current discovery result contains at least one concrete executable path that
    /// can be used by the sing-box process_path compiler. This is deliberately independent from
    /// launchability: a packaged or wrapper entry may be routable after discovery even when the
    /// controller cannot safely start it.
    /// </summary>
    public bool CanRoute
        => OwnedExecutables.Count > 0
            || (!string.IsNullOrWhiteSpace(CanonicalExecutable)
                && Path.GetExtension(CanonicalExecutable).Equals(".exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the launch service has a supported way to start this target.</summary>
    public bool CanLaunch
        => !ResolutionUnsupported
            && (Kind is LaunchKind.DirectExe or LaunchKind.CliNative
                ? !string.IsNullOrWhiteSpace(CanonicalExecutable ?? Command)
                : Kind == LaunchKind.PackagedAumid && !string.IsNullOrWhiteSpace(Aumid));

    private static string Prefix(string prefix, string? value, string? fallback)
        => string.IsNullOrWhiteSpace(value) ? prefix + (fallback ?? "") : prefix + value;
}
