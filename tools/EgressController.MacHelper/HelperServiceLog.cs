using System.Text;
using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.MacHelper;

/// <summary>Root-owned, bounded diagnostics. Never writes through a user-owned directory.</summary>
internal sealed class HelperServiceLog(NativeMacPrivileges native, bool networkDetails = false) : ILifecycleLog
{
    private string LogPath => networkDetails ? Path.Combine(MacHelperPaths.ServiceLogDirectory, "network-errors.log") : MacHelperPaths.ServiceLog;
    private readonly object _sync = new();
    private const int MaximumBytes = 256 * 1024;
    public void Append(LifecycleEvent entry)
    {
        lock (_sync)
        {
        HostFiles.PrivateDirectory(native, MacHelperPaths.ServiceLogDirectory, restrictToRoot: false);
        string previous = File.Exists(LogPath)
            ? Encoding.UTF8.GetString(HostFiles.ReadOwned(native, LogPath, 0, MaximumBytes)) : "";
        string text = previous + $"{entry.Time:O} [{OperationDiagnostics.SanitizeOutput(entry.Kind)}] {OperationDiagnostics.SanitizeOutput(entry.Cause)}\n";
        while (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
        {
            int boundary = text.IndexOf('\n');
            if (boundary < 0) return;
            text = text[(boundary + 1)..];
        }
        // Sanitized lifecycle data can be read by the GUI; only root can replace it.
        HostFiles.AtomicWrite(native, LogPath, Encoding.UTF8.GetBytes(text), 0x1a4);
        }
    }
}
