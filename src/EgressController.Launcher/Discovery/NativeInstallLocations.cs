using System.Runtime.InteropServices;
using Microsoft.Win32;
using EgressController.Core.Protection;

namespace EgressController.Launcher.Discovery;

internal static partial class NativeInstallLocations
{
    public static string? MsiLocation(string product, bool machine)
    {
        RuntimeSafety.RequireLiveOperations();
        if (!Guid.TryParse(product, out _)) return null;
        foreach (uint context in machine ? new uint[] { 4 } : [2u, 1u])
        {
            uint length = 0;
            uint result = GetProductInfo(product, null, context, "InstallLocation", null, ref length);
            if (result is not (0 or 234) || length is 0 or > 32767) continue;
            char[] buffer = new char[++length];
            if (GetProductInfo(product, null, context, "InstallLocation", buffer, ref length) == 0)
                return new string(buffer, 0, (int)length);
        }
        return null;
    }

    public static IReadOnlyList<InstalledApplication> TencentGames(List<string> notices)
    {
        RuntimeSafety.RequireLiveOperations();
        var result = new List<InstalledApplication>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (string vendor in new[] { @"Software\Tencent", @"Software\Rail" })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(vendor);
                if (root is null) continue;
                foreach (string name in root.GetSubKeyNames())
                    try
                    {
                        using var game = root.OpenSubKey(name);
                        string? path = game?.GetValue("InstallPath") as string ?? game?.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                        string? setup = game?.GetValue("setup_x64") as string ?? game?.GetValue("setup") as string;
                        // Install records never assign a user category.
                        result.Add(new("vendor:" + vendor.ToLowerInvariant() + ":" + name.ToLowerInvariant(), name.Equals("LOL", StringComparison.OrdinalIgnoreCase) ? "英雄联盟" : name,
                            path, setup, setup, null, "游戏平台安装登记"));
                    }
                    catch (Exception error) { notices.Add(name + " 安装登记不可读取：" + error.Message); }
            }
            catch (Exception error) { notices.Add(vendor + " 安装登记不可读取：" + error.Message); }
        }
        return result;
    }

    public static IReadOnlyList<string> SteamRoots(List<string> notices)
    {
        RuntimeSafety.RequireLiveOperations();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var steam = baseKey.OpenSubKey(@"Software\Valve\Steam");
                string? path = steam?.GetValue("InstallPath") as string ?? steam?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) result.Add(Path.GetFullPath(path));
            }
            catch (Exception error) { notices.Add("Steam 安装登记不可读取：" + error.Message); }
        return result.ToArray();
    }

    [DllImport("msi.dll", EntryPoint = "MsiGetProductInfoExW", CharSet = CharSet.Unicode)]
    private static extern uint GetProductInfo(string product, string? user, uint context, string property, [Out] char[]? value, ref uint size);
}
