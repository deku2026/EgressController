using System.Text.Json.Serialization;
using EgressController.Core.Models;
using EgressController.State.Storage;

namespace EgressController.State.Json;

/// <summary>
/// JSON source-generation context for the durable state documents (plan §Step 00/12). Keeps
/// serialization reflection-free and AOT-safe. Add new documents here as they are introduced.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ProxyStateRecord))]
[JsonSerializable(typeof(SystemProxyState))]
internal sealed partial class EgressStateJsonContext : JsonSerializerContext;

/// <summary>Cross-cutting accessor so State.Storage can use the generated context without exposing it.</summary>
internal static class StateJson
{
    public static EgressStateJsonContext Default => EgressStateJsonContext.Default;
}