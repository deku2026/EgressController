using EgressController.Core.Contracts;
using EgressController.Core.Models;
using Windows.Win32;
using Windows.Win32.Networking.WinInet;

namespace EgressController.Windows.SystemProxy;

/// <summary>
/// Acquires / restores the current-user System Proxy (plan §1.8 / §12).
///
/// Implements it over the **current-user Internet Settings registry** (the actual persisted state
/// that WinINet wraps), then pokes WinINet to refresh so running programs notice. Rationale
/// (§1.2 AOT / §12 robustness): avoids fragile INTERNET_PER_CONN_OPTION_LIST pointer-string
/// marshalling and LocalFree cleanup; the registry is the exact read-back-verifiable source and
/// is safe to snapshot/restore transactionally. Read-back uses the same manager so ownership is
/// semantic (SystemProxyStateComparer), not raw-string.
/// </summary>
public sealed class SystemProxyManager
{
    public const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const uint OptionPerConnection = 75;     // INTERNET_OPTION_PER_CONNECTION_OPTION
    private const uint OptionSettingsChanged = 39;   // INTERNET_OPTION_SETTINGS_CHANGED
    private const uint OptionRefresh = 37;           // INTERNET_OPTION_REFRESH

    public SystemProxyState Snapshot()
    {
        using var k = OpenKey(readWrite: false);
        int enabled = (k?.GetValue("ProxyEnable") as int?) ?? 0;
        string? server = k?.GetValue("ProxyServer") as string;
        string? bypass = k?.GetValue("ProxyOverride") as string;
        string? pac = k?.GetValue("AutoConfigURL") as string;
        int autodetect = (k?.GetValue("AutoDetect") as int?) ?? 0;
        return new SystemProxyState(enabled != 0, server, bypass, pac, autodetect != 0);
    }

    /// <summary>Apply a full state transactionally, then notify WinINet to refresh.</summary>
    public void Apply(SystemProxyState state)
    {
        using var k = OpenKey(readWrite: true);
        if (k is null)
            throw new InvalidOperationException($"cannot open {InternetSettingsKey}");
        k.SetValue("ProxyEnable", state.Enabled ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        k.SetValue("ProxyServer", state.Server ?? string.Empty, Microsoft.Win32.RegistryValueKind.String);
        k.SetValue("ProxyOverride", state.ProxyOverride ?? string.Empty, Microsoft.Win32.RegistryValueKind.String);
        if (state.AutoConfigUrl is null)
            if (k.GetValue("AutoConfigURL") is not null) k.DeleteValue("AutoConfigURL", false);
            else { /* already absent */ }
        else
            k.SetValue("AutoConfigURL", state.AutoConfigUrl, Microsoft.Win32.RegistryValueKind.String);
        k.SetValue("AutoDetect", state.AutoDetect ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        Refresh();
    }

    /// <summary>Poke WinINet so running apps re-read the proxy.</summary>
    public unsafe void Refresh()
    {
        _ = PInvoke.InternetSetOption(null, OptionSettingsChanged, (void*)null, 0);
        _ = PInvoke.InternetSetOption(null, OptionRefresh, (void*)null, 0);
    }

    public bool IsEquivalent(SystemProxyState a, SystemProxyState b)
        => SystemProxyStateComparer.StateEquivalent(a, b);

    public SystemProxyWatcher Watch(Action<SystemProxyState> onChanged)
        => new(this, onChanged);

    private static Microsoft.Win32.RegistryKey? OpenKey(bool readWrite)
        => Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InternetSettingsKey, readWrite) ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(InternetSettingsKey);
}
