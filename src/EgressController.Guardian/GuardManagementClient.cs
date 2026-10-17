using System.Diagnostics;
using EgressController.Core.Protection;

namespace EgressController.Guardian;

public enum GuardAction { Install, Start, Stop, ForceStop, StopAndDisable, EnableAutoStart, DisableAutoStart, Uninstall }
public sealed record GuardPanelSnapshot(GuardServiceInfo Service, GuardStatus? Guard, bool UserStopped, string? Error);
public interface IGuardManagementClient
{
    Task<GuardPanelSnapshot> ReadAsync(CancellationToken token = default);
    Task ExecuteAsync(GuardAction action, CancellationToken token = default);
    Task ClearHistoryAsync(CancellationToken token = default);
}

public sealed class WindowsGuardManagementClient : IGuardManagementClient
{
    private readonly GuardPaths _paths;
    private readonly Func<GuardPolicy>? _policy;
    public WindowsGuardManagementClient(GuardPaths paths, Func<GuardPolicy>? policy = null)
    {
        _paths = paths; _policy = policy;
    }
    public async Task<GuardPanelSnapshot> ReadAsync(CancellationToken token = default)
    {
        var registration = new GuardRegistrationStore(_paths.RegistrationFile).Load();
        var service = await Task.Run(() => new WindowsGuardServiceControl(_paths).Query(), token).ConfigureAwait(false);
        if (service.State == GuardServiceState.Missing) service = service with { AutoStart = registration?.AutoStart ?? true };
        GuardStatus? status = null;
        string? error = service.Error;
        if (service.State == GuardServiceState.Running)
        {
            try
            {
                await using var client = new GuardPipeClient(_paths);
                status = (await client.StatusAsync(token).ConfigureAwait(false)).Status;
            }
            catch (Exception exception) { error = "守护通信失败：" + exception.Message; }
        }
        return new(service, status, registration is { } value && (value.Uninstalled || value.StoppedOnBoot == GuardWindowsIdentity.BootId
            || !value.AutoStart && service.State == GuardServiceState.Stopped), error);
    }
    public async Task ExecuteAsync(GuardAction action, CancellationToken token = default)
    {
        RuntimeSafety.RequireLiveOperations();
        if (GuardWindowsIdentity.Elevated)
        {
            await Task.Run(() => ExecuteElevated(_paths, action, _policy?.Invoke()), token).ConfigureAwait(false);
            return;
        }
        var start = new ProcessStartInfo(_paths.TrayExecutable)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--manage"); start.ArgumentList.Add(action.ToString());
        start.ArgumentList.Add("--owner"); start.ArgumentList.Add(_paths.OwnerSid);
        using var helper = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("管理员操作未启动。");
        await helper.WaitForExitAsync(token).ConfigureAwait(false);
        if (helper.ExitCode != 0)
        {
            string errorFile = Path.Combine(_paths.DataDirectory, "management-error.log");
            throw new InvalidOperationException(File.Exists(errorFile) ? File.ReadAllText(errorFile) : "服务管理失败，错误码：" + helper.ExitCode);
        }
    }
    public async Task ClearHistoryAsync(CancellationToken token = default)
    {
        await using var client = new GuardPipeClient(_paths);
        await client.ClearAsync(token).ConfigureAwait(false);
    }
    public static void ExecuteElevated(GuardPaths paths, GuardAction action, GuardPolicy? policy = null)
    {
        RuntimeSafety.RequireLiveOperations();
        if (!GuardWindowsIdentity.Elevated) throw new UnauthorizedAccessException("需要管理员权限。");
        var store = new GuardRegistrationStore(paths.RegistrationFile);
        var registration = store.Load();
        string main = policy?.MainExecutable ?? registration?.MainExecutable ?? throw new InvalidOperationException("请从主程序首次安装守护。");
        var manager = new GuardManagement(store, new WindowsGuardPlatform(paths, Path.GetDirectoryName(main)!), GuardWindowsIdentity.BootId);
        switch (action)
        {
            case GuardAction.Install:
                policy ??= new GuardPolicyMailbox(paths.DataDirectory).Load()
                    ?? new GuardStore(paths.DataDirectory).LoadPolicy() ?? throw new InvalidOperationException("没有可恢复的保护名单，请打开主程序。");
                manager.Install(new() { OwnerSid = paths.OwnerSid, MainExecutable = main, InstallDirectory = paths.InstallDirectory,
                    AutoStart = registration?.AutoStart ?? true }, policy);
                break;
            case GuardAction.Start: manager.Start(); break;
            case GuardAction.Stop: manager.Stop(); break;
            case GuardAction.ForceStop: manager.Stop(force: true); break;
            case GuardAction.StopAndDisable: manager.Stop(disableAutoStart: true); break;
            case GuardAction.EnableAutoStart: manager.SetAutoStart(true); break;
            case GuardAction.DisableAutoStart: manager.SetAutoStart(false); break;
            case GuardAction.Uninstall: manager.Uninstall(); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}
