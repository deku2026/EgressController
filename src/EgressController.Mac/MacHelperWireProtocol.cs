// SPDX-License-Identifier: MIT
using System.Text.Json.Serialization;
using EgressController.Runtime;

namespace EgressController.Mac;

public enum MacHelperCommand { Inspect, Validate, Apply, StopAndRestore, Heartbeat, ResetAndRestore }
public sealed record MacHelperEndpoint(string SocketPath, string ExpectedHelperPath)
{
    public static MacHelperEndpoint Installed { get; } = new("/var/run/egresscontroller/helper.sock", "/Library/PrivilegedHelperTools/app.egresscontroller.mac.helper");
}
public static class MacHelperPaths
{
    public const string ServiceLogDirectory = "/Library/Logs/EgressController";
    public const string ServiceLog = ServiceLogDirectory + "/helper.log";
    public const string Label = "app.egresscontroller.mac.helper";
    public const string Installation = "/Library/Application Support/EgressController/Helper/installation.json";
    public const string PrivateRoot = "/Library/Application Support/EgressController/Helper";
    public const string LaunchDaemon = "/Library/LaunchDaemons/app.egresscontroller.mac.helper.plist";
    public const string NativeLibrary = "/Library/PrivilegedHelperTools/libEgressMacNative.dylib";
    public const string InstalledXray = "/Library/Application Support/EgressController/Helper/xray";
}
public sealed record MacPeerIdentity(uint UserId, uint GroupId, int ProcessId, long StartedAtMicroseconds, string ExecutablePath);
public sealed record MacFileSecurityInfo(uint UserId, uint GroupId, uint Mode, bool IsRegular, bool IsDirectory, bool IsSocket, bool IsSymbolicLink, long Size);
public sealed record MacRuntimeLaunch(string XrayPath, string XraySha256, string HomeServiceId, string NonHomeServiceId);
public sealed record MacHelperApplyPlan(string GenerationId, string GenerationDirectory, string ManifestSha256,
    string XrayPath, string XraySha256, string HomeServiceId, string NonHomeServiceId);
public sealed record MacRuntimeManifest(string GenerationId, Dictionary<string, string> Files);
public sealed record MacHelperWireRequest(int Version, Guid SessionId, long Sequence, MacHelperCommand Command, MacHelperApplyPlan? Apply = null);
public sealed record MacHelperStatus(bool Installed, bool Running, bool TunTakenOver, bool DnsRestored, int CorePid,
    string? GenerationId, string? Failure, DateTimeOffset ObservedAt, string? InstalledCoreSha256 = null);
public sealed record MacHelperWireReply(Guid SessionId, long Sequence, bool Succeeded, string Code, string Message, MacHelperStatus Status,
    [property: JsonConverter(typeof(MacConnectionSnapshotConverter))] CoreConnectionSnapshot? Connections = null);

/// <summary>Root-owned installation pins the only GUI and core identities allowed to request privilege.</summary>
public sealed record MacHelperInstallation(int Version, uint AuthorizedUserId, uint AuthorizedGroupId,
    string AuthorizedGuiPath, string AuthorizedGuiSha256, string UserStagingRoot,
    string XraySha256, string HelperSha256, string NativeLibrarySha256);
public sealed record MacHelperInstallPlan(string SourceHelperPath, string SourceNativeLibraryPath, string SourceXrayPath,
    MacHelperInstallation Installation);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MacHelperWireRequest))]
[JsonSerializable(typeof(MacHelperWireReply))]
[JsonSerializable(typeof(CoreConnectionSnapshot))]
[JsonSerializable(typeof(MacHelperInstallation))]
[JsonSerializable(typeof(MacHelperInstallPlan))]
[JsonSerializable(typeof(MacRuntimeManifest))]
[JsonSerializable(typeof(MacPeerIdentity))]
[JsonSerializable(typeof(MacFileSecurityInfo))]
public partial class MacHelperWireJson : JsonSerializerContext;
