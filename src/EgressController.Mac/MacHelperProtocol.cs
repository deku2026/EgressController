// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace EgressController.Mac;

public enum MacHelperOperation { Inspect, ApplyValidatedConfiguration, StopAndRestore }
public sealed record MacHelperRequest(int ProtocolVersion, Guid SessionId, long Sequence, MacHelperOperation Operation,
    string? ConfigurationPath, string? ConfigurationSha256, string? XrayPath, string? XraySha256,
    string? HomeServiceId, string? NonHomeServiceId);
public sealed record MacHelperReply(bool Succeeded, string Code, string Message, bool TunTakenOver, bool DnsRestored);
public sealed record MacAuthenticatedPeer(uint UserId, int ProcessId, long ProcessStartIdentity, bool ExecutableIdentityVerified);

/// <summary>Pure validation before any privileged adapter is invoked. No shell commands or arbitrary paths.</summary>
public static class MacHelperRequestValidator
{
    public const int MaximumFrameBytes = 1_048_576;
    public static string? Validate(MacHelperRequest request, MacAuthenticatedPeer peer, uint authorizedUserId,
        string configurationRoot, string coreRoot, Guid expectedSession, long previousSequence)
    {
        if (!peer.ExecutableIdentityVerified || peer.UserId != authorizedUserId || peer.ProcessId <= 0 || peer.ProcessStartIdentity <= 0) return "unauthenticated-peer";
        if (request.ProtocolVersion != 1 || request.SessionId == Guid.Empty || request.SessionId != expectedSession) return "invalid-session";
        if (request.Sequence <= previousSequence || request.Sequence <= 0) return "stale-sequence";
        if (!Enum.IsDefined(request.Operation)) return "unsupported-operation";
        if (request.Operation != MacHelperOperation.ApplyValidatedConfiguration)
            return request.ConfigurationPath is not null || request.ConfigurationSha256 is not null || request.XrayPath is not null
                || request.XraySha256 is not null || request.HomeServiceId is not null || request.NonHomeServiceId is not null ? "unexpected-payload" : null;
        if (!Guid.TryParse(request.HomeServiceId, out var home) || !Guid.TryParse(request.NonHomeServiceId, out var nonHome)
            || home == nonHome) return "invalid-interface-identity";
        if (!IsRestrictedPath(request.ConfigurationPath, configurationRoot, ".json") || !IsRestrictedPath(request.XrayPath, coreRoot, null)) return "outside-owned-directory";
        if (!IsHash(request.ConfigurationSha256) || !IsHash(request.XraySha256)) return "invalid-content-digest";
        return null;
    }

    public static bool ContentMatches(ReadOnlySpan<byte> content, string expectedSha256)
        => IsHash(expectedSha256) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), Convert.FromHexString(expectedSha256));

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool IsRestrictedPath(string? path, string root, string? extension)
    {
        if (!MacPath.IsAbsolute(path) || !MacPath.IsAbsolute(root) || MacPath.HasTraversal(path!) || MacPath.HasTraversal(root)) return false;
        return MacApplicationCatalog.IsInside(path, root) && (extension is null || path.EndsWith(extension, StringComparison.Ordinal));
    }
}

public interface IMacHelperTransport
{
    Task<MacHelperReply> SendAsync(MacHelperRequest request, CancellationToken cancellationToken = default);
}

/// <summary>No production helper server/installer is shipped yet. Never reports a simulated takeover as live.</summary>
public sealed class UnavailableMacHelperTransport : IMacHelperTransport
{
    public Task<MacHelperReply> SendAsync(MacHelperRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MacHelperReply(false, "helper-not-installed", "Privileged macOS helper is not implemented or installed. Preview cannot start a real TUN.", false, false));
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MacHelperRequest))]
[JsonSerializable(typeof(MacHelperReply))]
[JsonSerializable(typeof(MacDnsRecoverySnapshot))]
public partial class MacHelperJson : JsonSerializerContext;
