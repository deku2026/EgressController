// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using EgressController.Runtime;

namespace EgressController.Mac;

/// <summary>A malformed optional observation object cannot invalidate otherwise authenticated lifecycle facts.</summary>
public sealed class MacConnectionSnapshotConverter : JsonConverter<CoreConnectionSnapshot>
{
    public override CoreConnectionSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var payload = JsonDocument.ParseValue(ref reader);
        try { return payload.RootElement.Deserialize(MacHelperWireJson.Default.CoreConnectionSnapshot); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return null; }
    }
    public override void Write(Utf8JsonWriter writer, CoreConnectionSnapshot value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, MacHelperWireJson.Default.CoreConnectionSnapshot);
}
