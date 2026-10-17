using EgressController.Core.Protection;

namespace EgressController.Guardian;

/// <summary>Protocol authorization/state transitions are testable without a live named pipe.</summary>
public sealed class GuardRequestHandler(GuardProcess peer, string registeredMain, GuardRuntime runtime,
    Func<GuardPolicy?> desiredPolicy, Action wake) : IDisposable
{
    private Guid _session;
    public GuardReply Handle(GuardRequest request)
    {
        try
        {
            if (request.Protocol != 1) throw new InvalidDataException("不支持的守护通信版本。");
            bool main = string.Equals(peer.ExecutablePath, registeredMain, StringComparison.OrdinalIgnoreCase);
            void RequireMain()
            {
                if (!main || _session == Guid.Empty || request.Session != _session)
                    throw new UnauthorizedAccessException("没有有效主程序会话或消息已过期。");
            }
            GuardPolicy Policy()
            {
                var policy = request.Policy ?? throw new InvalidDataException("缺少保护名单。");
                if (!string.Equals(policy.MainExecutable, registeredMain, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("主程序路径不匹配。");
                if (desiredPolicy() is { } latest && latest.Revision != policy.Revision)
                    throw new InvalidDataException("保护名单消息已过期。");
                return policy;
            }
            switch (request.Kind)
            {
                case "hello":
                    if (!main || request.Session == Guid.Empty) throw new UnauthorizedAccessException("只有已登记主程序可以报告 TUN。");
                    runtime.OpenSession(request.Session, peer.Key, peer.ExecutablePath, Policy());
                    _session = request.Session;
                    wake();
                    break;
                case "policy":
                    RequireMain();
                    runtime.UpdatePolicy(_session, request.Sequence, Policy());
                    wake();
                    break;
                case "heartbeat":
                    if (!main || _session == Guid.Empty || request.Session != _session || request.Heartbeat is null
                        || !runtime.Report(_session, request.Sequence, request.Heartbeat))
                        throw new UnauthorizedAccessException("过期或无效的主程序状态。");
                    wake();
                    break;
                case "prepare":
                    RequireMain();
                    runtime.PrepareTransition(_session, request.Sequence);
                    break;
                case "status": break;
                case "clear": runtime.ClearHistory(); break;
                default: throw new InvalidDataException("未知守护请求。");
            }
            return new(true, null, runtime.Status);
        }
        catch (Exception error) { return new(false, error.Message, runtime.Status); }
    }
    public void Dispose()
    {
        if (_session == Guid.Empty) return;
        runtime.Disconnect(_session);
        _session = Guid.Empty;
        wake();
    }
}
