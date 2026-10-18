using System.Text.Json.Serialization;
using EgressController.Core.Protection;
using EgressController.Core.Contracts;

namespace EgressController.Guardian;

public sealed record GuardPolicy
{
    public string Revision { get; init; } = Guid.NewGuid().ToString("N");
    public string MainExecutable { get; init; } = "";
    public ProtectedExecutable[] Executables { get; init; } = [];
    public ProtectedDirectory[] Directories { get; init; } = [];
    public string[] Exclusions { get; init; } = [];
    public GuardPolicy WithPathAliases(IExecutablePathCanonicalizer resolver) => this with
    {
        Executables = Executables.SelectMany(item => ApplicationPathAliases.Expand([item.Path], resolver)
            .Select(path => item with { Path = path })).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
        Directories = Directories.SelectMany(item => ApplicationPathAliases.Expand([item.Path], resolver)
            .Select(path => item with { Path = path })).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
        Exclusions = ApplicationPathAliases.Expand(Exclusions, resolver),
    };

    public GuardPolicy Validate()
    {
        if (!Guid.TryParse(Revision, out _) || !Path.IsPathFullyQualified(MainExecutable))
            throw new InvalidDataException("守护配置版本或主程序路径无效。");
        if (Executables is null || Directories is null || Exclusions is null
            || Executables.Length + Directories.Length + Exclusions.Length > 20000)
            throw new InvalidDataException("守护名单过大或格式无效。");
        foreach (var item in Executables)
            if (item is null || !Path.IsPathFullyQualified(item.Path) || !Path.GetExtension(item.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("守护 EXE 必须是完整路径。");
        foreach (var root in Directories)
            if (root is null || !Path.IsPathFullyQualified(root.Path)
                || Path.GetPathRoot(Path.GetFullPath(root.Path)) == Path.GetFullPath(root.Path))
                throw new InvalidDataException("递归保护不能使用磁盘根目录。");
        if (Exclusions.Any(path => !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("保护排除项必须是完整路径。");
        return this with
        {
            MainExecutable = Path.GetFullPath(MainExecutable),
            Executables = Executables.Select(x => x with { Path = Path.GetFullPath(x.Path) }).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            Directories = Directories.Select(x => x with { Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Path)) }).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            Exclusions = Exclusions.Append(MainExecutable).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }
}

// Deliberately no Internet/DoH/latency verdict here. This protocol only reports TUN takeover.
public sealed record GuardHeartbeat(string PolicyRevision, bool TunReady, ProcessKey? Core, string? CorePath, string? Reason);
public sealed record GuardRequest
{
    public const int CurrentProtocol = 2; // v2 requires fixed EXE membership; v1 enrolled OS descendants.
    public int Protocol { get; init; } = CurrentProtocol;
    public string Kind { get; init; } = "";
    public Guid Session { get; init; }
    public long Sequence { get; init; }
    public GuardPolicy? Policy { get; init; }
    public GuardHeartbeat? Heartbeat { get; init; }
}
public sealed record GuardStatus(bool Protecting, string Reason, int TargetCount, int FailedCount,
    string? PolicyRevision, ProtectionEvent[] Events);
public sealed record GuardReply(bool Success, string? Error, GuardStatus? Status);
public sealed record GuardCheckpoint(TrackedProtectionProcess[] Processes, ProtectionEvent[] Events);
public sealed record GuardRegistration
{
    public int Schema { get; init; } = 1;
    public string OwnerSid { get; init; } = "";
    public string MainExecutable { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public bool Installed { get; init; }
    public bool AutoStart { get; init; } = true;
    public bool Uninstalled { get; init; }
    public string? StoppedOnBoot { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(GuardPolicy))]
[JsonSerializable(typeof(GuardRequest))]
[JsonSerializable(typeof(GuardReply))]
[JsonSerializable(typeof(GuardCheckpoint))]
[JsonSerializable(typeof(GuardRegistration))]
public sealed partial class GuardJson : JsonSerializerContext;
