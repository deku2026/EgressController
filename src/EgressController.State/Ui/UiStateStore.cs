using EgressController.State.Json;
using EgressController.State.Storage;

namespace EgressController.State.Ui;

public sealed record UiStateDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string ActivePage { get; init; } = "overview";
    public double WindowWidth { get; init; } = 1200;
    public double WindowHeight { get; init; } = 800;
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
    public string AppsSearch { get; init; } = string.Empty;
    public string DomainsSearch { get; init; } = string.Empty;
    public string ConnectionsSearch { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, double> ConnectionColumnWidths { get; init; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
}

public sealed class UiStateStore
{
    public UiStateStore(string baseDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        StatePath = Path.Combine(BaseDirectory, "ui-state.json");
    }

    public string BaseDirectory { get; }
    public string StatePath { get; }

    public UiStateDocument Load()
        => AtomicJsonFile.Read(
            StatePath,
            EgressStateJsonContext.Default.UiStateDocument,
            new UiStateDocument());

    public void Save(UiStateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != 1)
            throw new InvalidOperationException($"不支持的 UI state schemaVersion={state.SchemaVersion}。");
        if (state.WindowWidth is < 320 or > 10000 || state.WindowHeight is < 240 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(state), "窗口尺寸不在允许范围内。");

        AtomicJsonFile.Write(StatePath, state, EgressStateJsonContext.Default.UiStateDocument);
    }
}
