using EgressController.Core.Ipc;

namespace EgressController.ElevatedHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out string? pipeName, out int clientPid, out string? dataRoot, out string? systemCore))
            return 2;

        var policy = new ElevatedHostPathPolicy
        {
            DataRoot = dataRoot!,
            AllowedSystemCorePath = systemCore,
        };
        await using var processHost = new SingBoxProcessHost(policy);
        var server = new ElevatedHostServer(pipeName!, clientPid, policy, processHost);
        await server.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static bool TryParseArguments(
        string[] args,
        out string? pipeName,
        out int clientPid,
        out string? dataRoot,
        out string? systemCore)
    {
        pipeName = null;
        dataRoot = null;
        systemCore = null;
        clientPid = 0;
        if (args.Length is < 6 or > 8 || args.Length % 2 != 0)
            return false;
        for (int i = 0; i < args.Length; i += 2)
        {
            string key = args[i];
            string value = args[i + 1];
            if (string.IsNullOrWhiteSpace(value))
                return false;
            switch (key)
            {
                case "--pipe" when pipeName is null && value.Length <= 200 && !value.Contains('\\'):
                    pipeName = value;
                    break;
                case "--client-pid" when clientPid == 0 && int.TryParse(value, out int parsedPid) && parsedPid > 0:
                    clientPid = parsedPid;
                    break;
                case "--data-root" when dataRoot is null && Path.IsPathRooted(value):
                    dataRoot = Path.GetFullPath(value);
                    break;
                case "--system-core" when systemCore is null && Path.IsPathRooted(value):
                    systemCore = Path.GetFullPath(value);
                    break;
                default:
                    return false;
            }
        }
        return pipeName is not null && clientPid > 0 && dataRoot is not null;
    }
}
