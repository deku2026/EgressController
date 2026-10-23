using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Launcher.Discovery;

namespace EgressController.App.Services;

/// <summary>Shared by the UI and launch-time validation. No discovery, network tests or mutations.</summary>
public static class ApplicationLaunchPolicy
{
    public static bool IsCli(LaunchTarget target) => target.Kind is LaunchKind.CliNative or LaunchKind.CliWrapperResolved;

    public static string? BlockReason(LaunchTarget? target, EgressProfileDocument profile,
        bool tunRunning, bool updating, ProtectionReadiness protection)
    {
        if (target is null) return "应用已不在列表中，请刷新列表。";
        if (IsCli(target)) return "CLI 请在终端中启动。";
        var selected = profile.Applications.Where(item => target.SelectionKeys.Contains(item.DiscoveryKey)).ToArray();
        if (selected.Length == 0) return "请先勾选这个应用。";
        if (updating) return "正在保存分流，请稍后启动。";
        if (!tunRunning) return "TUN 尚未接管，暂不能启动受保护的应用。";
        if (!protection.Ready) return "保护尚未就绪：" + protection.Reason;
        if (!target.InventoryReady) return "请先刷新 EXE 清单，应用新增的程序路径。";
        if (!target.CanLaunch || target.Source == "已保存的程序清单") return "尚未确定主入口，请刷新列表或指定主程序 EXE。";
        if (target.Kind is LaunchKind.DirectExe or LaunchKind.PackagedAumid)
        {
            string? executable = target.CanonicalExecutable ?? target.Command;
            if (string.IsNullOrWhiteSpace(executable)) return "尚未确定主程序路径，请刷新列表。";
            if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || InstalledApplicationResolver.IsInstaller(executable))
                return "主入口无效，请指定应用主程序 EXE。";
            if (!selected.SelectMany(item => item.ExecutablePaths).Contains(executable, StringComparer.OrdinalIgnoreCase))
                return "主程序尚未加入保存的保护清单，请刷新 EXE 清单。";
        }
        return null;
    }
}
