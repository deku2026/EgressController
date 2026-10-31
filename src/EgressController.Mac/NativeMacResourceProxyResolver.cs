// SPDX-License-Identifier: MIT
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EgressController.Runtime;

namespace EgressController.Mac;

public sealed record MacStaticProxyEntry(string Type, string? Host = null, int? Port = null, string? Username = null, string? Password = null)
{
    public override string ToString() => "MacStaticProxyEntry { proxy settings redacted }";
}
public sealed record MacStaticProxySnapshot(MacStaticProxyEntry[] Proxies);

/// <summary>Pure decoding of ordered static CFNetwork decisions. It never executes PAC or tries a later route.</summary>
public static class MacStaticProxyDecision
{
    public const int MaximumSnapshotBytes = 256 * 1024;
    public static IWebProxy? Decode(ReadOnlyMemory<byte> payload)
    {
        try
        {
            if (payload.Length is < 1 or > MaximumSnapshotBytes) throw Invalid();
            var snapshot = JsonSerializer.Deserialize(payload.Span, MacProxyJson.Default.MacStaticProxySnapshot);
            if (snapshot?.Proxies is not { Length: > 0 and <= 64 } entries || entries[0] is not { } selected) throw Invalid();
            if (selected.Type == "direct")
            {
                if (selected.Host is not null || selected.Port is not null || selected.Username is not null || selected.Password is not null) throw Invalid();
                return null;
            }
            if (selected.Type == "pac") throw new ResourceProxyConfigurationException(ResourceProxyFailureKind.PacUnsupported);
            string scheme = selected.Type switch { "http" or "https" => "http", "socks" => "socks5", _ => throw Invalid() };
            // CF kCFProxyTypeHTTPS denotes a CONNECT tunneling HTTP proxy, not TLS to the proxy.
            if (!SafeHost(selected.Host) || selected.Port is not (> 0 and <= 65535)
                || !SafeCredential(selected.Username) || !SafeCredential(selected.Password)) throw Invalid();
            var address = new UriBuilder(scheme, selected.Host!, selected.Port.Value).Uri;
            return new WebProxy(address) { Credentials = selected.Username is null && selected.Password is null ? null
                : new NetworkCredential(selected.Username ?? "", selected.Password ?? "") };
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
        { throw Invalid(); }
    }
    internal static bool SafeHost(string? host) => host is { Length: > 0 and <= 253 } && !host.Any(char.IsControl)
        && !host.Any(char.IsWhiteSpace) && !host.Contains('%') && !host.Contains('@') && !host.Contains('/') && !host.Contains('\\')
        && Uri.CheckHostName(host.Trim('[', ']')) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    internal static bool SafeCredential(string? value) => value is null || value.Length <= 1024 && !value.Any(char.IsControl);
    internal static ResourceProxyConfigurationException Invalid() => new(ResourceProxyFailureKind.InvalidSettings);
}

/// <summary>Applicable HTTPS/ALL environment settings, parsed without touching HttpClient.DefaultProxy or macOS PAC.</summary>
public static class MacEnvironmentResourceProxy
{
    public static bool TryResolve(Uri destination, Func<string, string?> environment, out IWebProxy? proxy)
    {
        proxy = null;
        string? configured = Preferred("https_proxy", "HTTPS_PROXY") ?? Preferred("all_proxy", "ALL_PROXY");
        if (configured is null) return false;
        try
        {
            if (configured.Length is < 1 or > 4096 || configured.Any(char.IsControl) || configured.Any(char.IsWhiteSpace)) throw MacStaticProxyDecision.Invalid();
            string value = configured.Contains("://", StringComparison.Ordinal) ? configured : "http://" + configured;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")
                || !MacStaticProxyDecision.SafeHost(address.Host) || address.Port is <= 0 or > 65535
                || address.AbsolutePath is not ("" or "/") || address.Query.Length != 0 || address.Fragment.Length != 0) throw MacStaticProxyDecision.Invalid();
            string? bypass = environment("no_proxy") ?? environment("NO_PROXY");
            if (bypass?.Length > 8192 || bypass?.Any(char.IsControl) == true) throw MacStaticProxyDecision.Invalid();
            var captured = new EnvironmentProxy(address, bypass);
            if (captured.IsBypassed(destination)) return true;
            proxy = new WebProxy(captured.GetProxy(destination)) { Credentials = captured.Credentials };
            return true;
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException or InvalidOperationException)
        { throw MacStaticProxyDecision.Invalid(); }

        string? Preferred(string lower, string upper)
        {
            // Empty values do not override a configured uppercase value; malformed nonempty values fail closed.
            string? value = environment(lower); return !string.IsNullOrEmpty(value) ? value : EmptyToNull(environment(upper));
        }
        static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }

    private sealed class EnvironmentProxy : IWebProxy
    {
        private readonly Uri _address;
        private readonly string[] _bypass;
        public EnvironmentProxy(Uri address, string? bypass)
        {
            var authority = new UriBuilder(address) { UserName = "", Password = "" }; _address = authority.Uri;
            _bypass = bypass?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (address.UserInfo.Length == 0) return;
            int delimiter = address.UserInfo.IndexOf(':');
            string user = Uri.UnescapeDataString(delimiter < 0 ? address.UserInfo : address.UserInfo[..delimiter]);
            string password = delimiter < 0 ? "" : Uri.UnescapeDataString(address.UserInfo[(delimiter + 1)..]);
            if (!MacStaticProxyDecision.SafeCredential(user) || !MacStaticProxyDecision.SafeCredential(password)) throw MacStaticProxyDecision.Invalid();
            Credentials = new NetworkCredential(user, password);
        }
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => IsBypassed(destination) ? destination : _address;
        // Same exact-host / leading-dot suffix behavior as .NET's HttpEnvironmentProxy.IsBypassed.
        public bool IsBypassed(Uri destination) => _bypass.Any(item => item.StartsWith('.')
            ? destination.Host.Equals(item[1..], StringComparison.OrdinalIgnoreCase) || destination.Host.EndsWith(item, StringComparison.OrdinalIgnoreCase)
            : destination.Host.Equals(item, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Production static proxy resolver. Native reads are gated, and testing injects only serialized snapshots.</summary>
public sealed unsafe class NativeMacResourceProxyResolver : ISystemResourceProxyResolver
{
    private readonly Func<Uri, ReadOnlyMemory<byte>> _snapshot;
    private readonly Lazy<Dictionary<string, string?>> _environment;
    private readonly Action? _requireLive;
    public NativeMacResourceProxyResolver(LiveOperationGate gate)
        : this(CaptureNativeSettings, ResolveNativeSettings, Environment.GetEnvironmentVariable)
    {
        _requireLive = () => gate.Require("resolve system resource proxy");
    }
    /// <summary>Static JSON fixture constructor; its first supplied decision is detached and reused.</summary>
    public NativeMacResourceProxyResolver(Func<Uri, ReadOnlyMemory<byte>> snapshotReader, Func<string, string?> environmentReader)
    {
        _environment = CaptureEnvironment(environmentReader);
        var sync = new object(); byte[]? captured = null;
        _snapshot = destination => { lock (sync) return captured ??= snapshotReader(destination).ToArray(); };
    }
    /// <summary>Injected captured-settings/URL-decision fixtures. All returned settings are detached before reuse.</summary>
    public NativeMacResourceProxyResolver(Func<ReadOnlyMemory<byte>> captureSettings,
        Func<Uri, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> resolveWithSettings, Func<string, string?> environmentReader)
    {
        _environment = CaptureEnvironment(environmentReader);
        var captured = new Lazy<byte[]>(() =>
        {
            var bytes = captureSettings();
            if (bytes.Length is < 1 or > MacStaticProxyDecision.MaximumSnapshotBytes) throw MacStaticProxyDecision.Invalid();
            return bytes.ToArray();
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _snapshot = destination => resolveWithSettings(destination, captured.Value);
    }
    private static Lazy<Dictionary<string, string?>> CaptureEnvironment(Func<string, string?> reader)
        => new(() => new[] { "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY", "no_proxy", "NO_PROXY" }
            .ToDictionary(key => key, reader, StringComparer.Ordinal), LazyThreadSafetyMode.ExecutionAndPublication);
    public IWebProxy? Resolve(Uri destination)
    {
        _requireLive?.Invoke();
        if (!destination.IsAbsoluteUri || destination.Scheme != "https" || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0
            || destination.AbsoluteUri.Length > 8192) throw new ArgumentException("A bounded HTTPS resource URL is required.", nameof(destination));
        var environment = _environment.Value;
        if (MacEnvironmentResourceProxy.TryResolve(destination, key => environment.GetValueOrDefault(key), out var proxy)) return proxy;
        ReadOnlyMemory<byte> payload;
        try { payload = _snapshot(destination); }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw MacStaticProxyDecision.Invalid(); }
        return MacStaticProxyDecision.Decode(payload);
    }
    private static ReadOnlyMemory<byte> CaptureNativeSettings()
    {
        // A fixed bounded destination performs exactly one live settings read, rather than a size query and a second capture.
        var bytes = new byte[MacStaticProxyDecision.MaximumSnapshotBytes];
        nuint required = 0;
        int status;
        fixed (byte* output = bytes) status = NativeResourceProxyMethods.Capture(output, (nuint)bytes.Length, &required);
        if (status != 0 || required is 0 or > MacStaticProxyDecision.MaximumSnapshotBytes) throw MacStaticProxyDecision.Invalid();
        return bytes.AsMemory(0, (int)required);
    }
    private static ReadOnlyMemory<byte> ResolveNativeSettings(Uri destination, ReadOnlyMemory<byte> settings)
    {
        nuint required = 0;
        int status;
        fixed (byte* source = settings.Span)
            status = NativeResourceProxyMethods.Resolve(destination.AbsoluteUri, source, (nuint)settings.Length, null, 0, &required);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (status is not (0 or 1) || required is 0 or > MacStaticProxyDecision.MaximumSnapshotBytes) throw MacStaticProxyDecision.Invalid();
            var bytes = new byte[(int)required];
            fixed (byte* source = settings.Span)
            fixed (byte* output = bytes)
                status = NativeResourceProxyMethods.Resolve(destination.AbsoluteUri, source, (nuint)settings.Length, output, (nuint)bytes.Length, &required);
            if (status == 1) continue;
            if (status != 0 || required > (nuint)bytes.Length) throw MacStaticProxyDecision.Invalid();
            return bytes.AsSpan(0, (int)required).ToArray();
        }
        throw MacStaticProxyDecision.Invalid();
    }
}

internal static unsafe partial class NativeResourceProxyMethods
{
    [LibraryImport("EgressMacNative", EntryPoint = "egress_mac_copy_resource_proxy_settings_plist")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Capture(byte* output, nuint capacity, nuint* required);
    [LibraryImport("EgressMacNative", EntryPoint = "egress_mac_resolve_resource_proxy_json", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Resolve(string url, byte* settings, nuint length, byte* output, nuint capacity, nuint* required);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MacStaticProxySnapshot))]
public partial class MacProxyJson : JsonSerializerContext;
