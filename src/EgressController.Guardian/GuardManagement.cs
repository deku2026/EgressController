namespace EgressController.Guardian;

public enum GuardServiceState { Missing, Stopped, Starting, Running, Stopping, Unknown }
public sealed record GuardServiceInfo(string Name, GuardServiceState State, bool AutoStart, uint Pid,
    string BinaryPath, string Account, string? Error = null);
public interface IGuardRegistrationStore
{
    GuardRegistration? Load();
    void Save(GuardRegistration value);
}
public interface IGuardServicePlatform
{
    GuardServiceInfo Query();
    void Install(GuardRegistration registration, GuardPolicy policy);
    void Start();
    void Stop(bool force);
    void SetAutoStart(bool enabled);
    void Uninstall();
    void ConfigureTray(bool enabled);
    void StartTray();
}

/// <summary>Explicit user actions win over automatic startup and recovery.</summary>
public sealed class GuardManagement(IGuardRegistrationStore store, IGuardServicePlatform platform, string bootId)
{
    private readonly object _gate = new();
    public GuardRegistration? Registration => store.Load();
    public GuardServiceInfo Query() => platform.Query();

    public void EnsureForMain(GuardRegistration initial, GuardPolicy policy)
    {
        lock (_gate)
        {
            GuardRegistration? registration = store.Load();
            if (registration is null) Install(initial, policy);
            else if (registration.Installed && !registration.Uninstalled
                && registration.AutoStart && registration.StoppedOnBoot != bootId)
            {
                var status = platform.Query();
                if (status.State == GuardServiceState.Missing)
                    throw new InvalidOperationException("守护服务缺失，请在服务管理中显式安装/修复。");
                if (status.State == GuardServiceState.Stopped) platform.Start();
            }
            // The tray is an independent management entry even when the service was stopped.
            if (store.Load() is { Installed: true, Uninstalled: false }) platform.StartTray();
        }
    }
    public void Install(GuardRegistration initial, GuardPolicy policy)
    {
        lock (_gate)
        {
            var registration = initial with { Installed = true, Uninstalled = false, StoppedOnBoot = null };
            platform.Install(registration, policy);
            store.Save(registration);
            platform.ConfigureTray(registration.AutoStart);
            platform.Start();
        }
    }
    public void Start()
    {
        lock (_gate)
        {
            var registration = Required();
            if (registration.Uninstalled || !registration.Installed) throw new InvalidOperationException("请先安装守护服务。");
            store.Save(registration with { StoppedOnBoot = null });
            platform.Start();
        }
    }
    public void Stop(bool force = false, bool disableAutoStart = false)
    {
        lock (_gate)
        {
            var registration = Required();
            // Persist intent before SCM STOP/force termination; crash recovery must honor it.
            store.Save(registration with { StoppedOnBoot = bootId });
            if (disableAutoStart) SetAutoStart(false);
            platform.Stop(force);
        }
    }
    public void SetAutoStart(bool enabled)
    {
        lock (_gate)
        {
            var registration = Required();
            store.Save(registration with { AutoStart = enabled });
            platform.SetAutoStart(enabled);
            platform.ConfigureTray(enabled);
        }
    }
    public void Uninstall()
    {
        lock (_gate)
        {
            var registration = Required();
            store.Save(registration with { Uninstalled = true, Installed = false, AutoStart = false, StoppedOnBoot = bootId });
            platform.ConfigureTray(false);
            platform.Uninstall();
        }
    }
    private GuardRegistration Required() => store.Load() ?? throw new InvalidOperationException("尚未安装独立守护。");
}
