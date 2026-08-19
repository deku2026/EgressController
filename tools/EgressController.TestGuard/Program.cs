using System.Diagnostics;
using EgressController.Core.Models;
using EgressController.Windows.SystemProxy;

// TestGuard: outer guard for destructive tests (plan §3.6).
//   snapshot System Proxy -> run <command> -- ... -> in a `finally` restore the snapshot + verify.
// Even if the child crashes/exits abnormally, the System Proxy is put back exactly as found.
// usage: TestGuard -- <command> [args...]

int sep = Array.IndexOf(args, "--");
if (sep < 0 || sep >= args.Length - 1)
{
    Console.Error.WriteLine("usage: TestGuard -- <command> [args...]");
    return 2;
}

string file = Path.GetFullPath(args[sep + 1]);
string childArgs = string.Join(' ', args[(sep + 2)..]);

var manager = new SystemProxyManager();
SystemProxyState before = manager.Snapshot();
Console.WriteLine($"[guard] snapshot      enabled={before.Enabled} server={before.Server ?? "null"}");

int childExit = -1;
try
{
    var psi = new ProcessStartInfo { FileName = file, Arguments = childArgs, UseShellExecute = false };
    using var proc = Process.Start(psi);
    proc!.WaitForExit();
    childExit = proc.ExitCode;
    Console.WriteLine($"[guard] child exit      = {childExit}");
}
finally
{
    // Restore regardless of how the child ended (including crash/abnormal exit).
    manager.Apply(before);
    SystemProxyState after = manager.Snapshot();
    bool equivalent = SystemProxyStateComparer.StateEquivalent(before, after);
    Console.WriteLine($"[guard] restored       enabled={after.Enabled} server={after.Server ?? "null"} equivalent={equivalent}");
    if (!equivalent)
    {
        Console.Error.WriteLine("[guard] !! proxy did not round-trip to snapshot");
        Environment.ExitCode = 9;
    }
}

return childExit;