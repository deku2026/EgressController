namespace EgressController.Guardian;

public static class GuardPipeAccess
{
    // Individual file rights, deliberately without FILE_CREATE_PIPE_INSTANCE (0x4).
    // GENERIC_WRITE would implicitly request that bit and deny an ordinary tray client.
    public const uint ClientRights = 0x12019b;
    public const uint ClientFlags = 0x40000000 | 0x00100000 | 0x00010000; // Overlapped; identification only.
    public static string Descriptor(string ownerSid)
        => $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x{ClientRights:x};;;{ownerSid})";

    public static void ValidateServer(GuardPaths paths, uint pipeServerPid, GuardServiceInfo service)
    {
        // Ordinary tray users can query SCM, but cannot open a LocalSystem token.
        // Tie the kernel-reported pipe endpoint to the installed SCM identity instead.
        bool system = service.Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || service.Account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
        if (service.Name != paths.ServiceName || service.State is not (GuardServiceState.Running or GuardServiceState.Starting)
            || pipeServerPid == 0 || service.Pid != pipeServerPid || !system
            || !service.BinaryPath.Equals(paths.ServiceCommandLine, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("守护通信端点与已安装服务身份不匹配。");
    }
}
