using EgressController.State.Json;
using EgressController.State.Storage;

namespace EgressController.State.SingBox;

public sealed record SingBoxCorePointer
{
    public required string Version { get; init; }
    public required string ExecutablePath { get; init; }
    public required string Sha256 { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
}

public sealed record SingBoxRuntimePointer
{
    public required SingBoxCorePointer Core { get; init; }
    public required string ConfigPath { get; init; }
    public required string ConfigSha256 { get; init; }
    public required DateTimeOffset AppliedAtUtc { get; init; }
    public int ControllerPort { get; init; }
    public string ControllerSecret { get; init; } = string.Empty;
}

public sealed record SingBoxPendingApply
{
    public required SingBoxRuntimePointer Candidate { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class SingBoxStateStore
{
    public SingBoxStateStore(string baseDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        CoreDirectory = Path.Combine(BaseDirectory, "core");
        CurrentPath = Path.Combine(CoreDirectory, "current-core.json");
        LastGoodPath = Path.Combine(CoreDirectory, "last-good-core.json");
        CurrentRuntimePath = Path.Combine(BaseDirectory, "current-runtime.json");
        LastGoodRuntimePath = Path.Combine(BaseDirectory, "last-good-runtime.json");
        PendingApplyPath = Path.Combine(BaseDirectory, "apply.pending.json");
    }

    public string BaseDirectory { get; }
    public string CoreDirectory { get; }
    public string CurrentPath { get; }
    public string LastGoodPath { get; }
    public string CurrentRuntimePath { get; }
    public string LastGoodRuntimePath { get; }
    public string PendingApplyPath { get; }

    public SingBoxCorePointer? LoadCurrent()
        => LoadCore(CurrentPath);

    public SingBoxCorePointer? LoadLastGood()
        => LoadCore(LastGoodPath);

    public void SaveCurrent(SingBoxCorePointer pointer)
        => Save(CurrentPath, pointer);

    public void SaveLastGood(SingBoxCorePointer pointer)
        => Save(LastGoodPath, pointer);

    public SingBoxRuntimePointer? LoadCurrentRuntime()
        => LoadRuntime(CurrentRuntimePath);

    public SingBoxRuntimePointer? LoadLastGoodRuntime()
        => LoadRuntime(LastGoodRuntimePath);

    public SingBoxPendingApply? LoadPendingApply()
        => LoadPending(PendingApplyPath);

    public void SaveCurrentRuntime(SingBoxRuntimePointer pointer)
        => Save(CurrentRuntimePath, pointer, EgressStateJsonContext.Default.SingBoxRuntimePointer);

    public void SaveLastGoodRuntime(SingBoxRuntimePointer pointer)
        => Save(LastGoodRuntimePath, pointer, EgressStateJsonContext.Default.SingBoxRuntimePointer);

    public void SavePendingApply(SingBoxPendingApply pending)
        => Save(PendingApplyPath, pending, EgressStateJsonContext.Default.SingBoxPendingApply);

    public void ClearPendingApply()
    {
        if (File.Exists(PendingApplyPath))
            File.Delete(PendingApplyPath);
    }

    private static SingBoxCorePointer? LoadCore(string path)
    {
        if (!File.Exists(path))
            return null;
        return AtomicJsonFile.Read(
            path,
            EgressStateJsonContext.Default.SingBoxCorePointer,
            (SingBoxCorePointer?)null);
    }

    private static SingBoxRuntimePointer? LoadRuntime(string path)
    {
        if (!File.Exists(path))
            return null;
        return AtomicJsonFile.Read(
            path,
            EgressStateJsonContext.Default.SingBoxRuntimePointer,
            (SingBoxRuntimePointer?)null);
    }

    private static SingBoxPendingApply? LoadPending(string path)
    {
        if (!File.Exists(path))
            return null;
        return AtomicJsonFile.Read(
            path,
            EgressStateJsonContext.Default.SingBoxPendingApply,
            (SingBoxPendingApply?)null);
    }

    private static void Save(string path, SingBoxCorePointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        AtomicJsonFile.Write(path, pointer, EgressStateJsonContext.Default.SingBoxCorePointer);
    }

    private static void Save<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(value);
        AtomicJsonFile.Write(path, value, typeInfo);
    }
}
