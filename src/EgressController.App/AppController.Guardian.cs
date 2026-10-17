using EgressController.Guardian;
using EgressController.Core.Protection;
using EgressController.Windows.Process;

namespace EgressController.App;

public sealed partial class AppController
{
    private MainGuardAgent? _guardAgent;
    private volatile bool _guardTransition;
    public GuardManagementViewModel Guard { get; private set; } = null!;
    public string GuardianConnectionStatus => _guardAgent?.Status ?? "独立守护未初始化";
    private GuardPolicy CurrentGuardPolicy()
        => GuardPolicyMailbox.FromProfile(_profile, Environment.ProcessPath!,
            _excludedPaths.Concat(new[] { Environment.ProcessPath, _coreExecutable }.OfType<string>()));
    private void InitializeGuardian()
    {
        GuardPaths paths = GuardPaths.Current;
        Guard = new(new WindowsGuardManagementClient(paths, CurrentGuardPolicy));
        _guardAgent = new(paths, CurrentGuardPolicy, CaptureGuardHeartbeat);
        _guardAgent.Start();
    }
    private GuardHeartbeat CaptureGuardHeartbeat(GuardPolicy policy)
    {
        // No NetworkHealthState or ProtectionReadiness here: the companion monitors TUN only.
        if (_shuttingDown || _guardTransition || !IsTunRunning || !_profileApplied)
            return new(policy.Revision, false, null, null, "TUN 尚未接管或正在变更");
        var observed = _singBox.Status;
        var status = _directSingBox.Status;
        if (status.ProcessId is not int pid || status.State != "running")
            return new(policy.Revision, false, null, null, "受管理 sing-box 未运行");
        var identity = new WindowsProcessIdentityResolver(new ExecutablePathCanonicalizer()).Resolve((uint)pid);
        string? error = identity?.ExePathFinal is null ? "无法确认 sing-box 实例" : _tunInspector.GetError();
        if (error is not null)
        {
            _singBox.ReportTakeoverFailure(error, observed);
            return new(policy.Revision, false, null, null, error);
        }
        return new(policy.Revision, true, new((uint)pid, identity!.StartTimeUtc), identity.ExePathFinal, null);
    }
}
