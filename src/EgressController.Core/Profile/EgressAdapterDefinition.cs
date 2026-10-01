namespace EgressController.Core.Profile;

/// <summary>A named, stable Windows interface selection. Never persist an interface index.</summary>
public sealed record EgressAdapterDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}
