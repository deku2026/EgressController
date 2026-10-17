namespace EgressController.Guardian;

public interface IMainGuardClient : IAsyncDisposable
{
    Task<GuardReply> SyncAsync(GuardPolicy policy, CancellationToken token = default);
    Task<GuardReply> HeartbeatAsync(GuardPolicy policy, GuardHeartbeat heartbeat, CancellationToken token = default);
    Task<GuardReply> PrepareAsync(GuardPolicy policy, CancellationToken token = default);
}

public interface IMainGuardEnvironment
{
    void Initialize(GuardPolicy policy);
    bool UserDisabled { get; }
    void SaveDesired(GuardPolicy policy);
}

public sealed class WindowsMainGuardEnvironment(GuardPaths paths) : IMainGuardEnvironment
{
    private readonly GuardRegistrationStore _registrations = new(paths.RegistrationFile);
    private readonly GuardPolicyMailbox _mailbox = new(paths.DataDirectory);
    private readonly Lazy<string> _boot = new(() => GuardWindowsIdentity.BootId);
    public void Initialize(GuardPolicy policy)
    {
        var manager = new GuardManagement(_registrations,
            new WindowsGuardPlatform(paths, Path.GetDirectoryName(policy.MainExecutable)!), _boot.Value);
        manager.EnsureForMain(new() { OwnerSid = paths.OwnerSid, MainExecutable = policy.MainExecutable,
            InstallDirectory = paths.InstallDirectory }, policy);
    }
    public bool UserDisabled
    {
        get
        {
            var value = _registrations.Load();
            return value is not null && (value.Uninstalled || value.StoppedOnBoot == _boot.Value
                || !value.AutoStart && new WindowsGuardServiceControl(paths).Query().State == GuardServiceState.Stopped);
        }
    }
    public void SaveDesired(GuardPolicy policy)
    {
        // Installation establishes machine ACLs before creating this registration. Never
        // manufacture writable ProgramData state after a failed/missing payload installation.
        var registration = _registrations.Load() ?? throw new InvalidOperationException("独立守护尚未安装，请先安装完整发布包。");
        if (registration.OwnerSid != paths.OwnerSid
            || !string.Equals(registration.MainExecutable, policy.MainExecutable, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(registration.InstallDirectory, paths.InstallDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("主程序位置与守护安装记录不同，请先卸载守护，再从当前主程序重新安装。");
        _mailbox.Save(policy);
    }
}
