using System.Text.Json.Serialization;
using EgressController.Core.Profile;
using EgressController.Core.Models;
using EgressController.State.Storage;
using EgressController.State.SingBox;
using EgressController.State.Ui;

namespace EgressController.State.Json;

/// <summary>
/// JSON source-generation context for the durable state documents (plan §Step 00/12). Keeps
/// serialization reflection-free and AOT-safe. Add new documents here as they are introduced.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ProxyStateRecord))]
[JsonSerializable(typeof(SystemProxyState))]
[JsonSerializable(typeof(EgressProfileDocument))]
[JsonSerializable(typeof(EgressCoreSelection))]
[JsonSerializable(typeof(EgressApplicationSelection))]
[JsonSerializable(typeof(UiStateDocument))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(SingBoxCorePointer))]
internal sealed partial class EgressStateJsonContext : JsonSerializerContext;

/// <summary>Cross-cutting accessor so State.Storage can use the generated context without exposing it.</summary>
internal static class StateJson
{
    public static EgressStateJsonContext Default => EgressStateJsonContext.Default;
}
