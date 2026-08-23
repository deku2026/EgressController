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

public sealed class SingBoxStateStore
{
    public SingBoxStateStore(string baseDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        CoreDirectory = Path.Combine(BaseDirectory, "core");
        CurrentPath = Path.Combine(CoreDirectory, "current-core.json");
        LastGoodPath = Path.Combine(CoreDirectory, "last-good-core.json");
    }

    public string BaseDirectory { get; }
    public string CoreDirectory { get; }
    public string CurrentPath { get; }
    public string LastGoodPath { get; }

    public SingBoxCorePointer? LoadCurrent()
        => Load(CurrentPath);

    public SingBoxCorePointer? LoadLastGood()
        => Load(LastGoodPath);

    public void SaveCurrent(SingBoxCorePointer pointer)
        => Save(CurrentPath, pointer);

    public void SaveLastGood(SingBoxCorePointer pointer)
        => Save(LastGoodPath, pointer);

    private static SingBoxCorePointer? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        return AtomicJsonFile.Read(path, EgressStateJsonContext.Default.SingBoxCorePointer, (SingBoxCorePointer?)null);
    }

    private static void Save(string path, SingBoxCorePointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        AtomicJsonFile.Write(path, pointer, EgressStateJsonContext.Default.SingBoxCorePointer);
    }
}
