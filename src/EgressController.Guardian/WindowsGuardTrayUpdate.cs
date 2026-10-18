using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Protection;
using EgressController.Windows.Process;

namespace EgressController.Guardian;

/// <summary>Same installed filename; stage, verify, stop exact owned UI instances, then swap directories.</summary>
internal sealed class WindowsGuardTrayUpdate(GuardPaths paths, string sourceDirectory) : IGuardTrayUpdatePlatform
{
    private readonly string _source = Path.GetFullPath(Path.Combine(sourceDirectory, "guard-tray"));
    private readonly string _destination = Path.GetFullPath(Path.Combine(paths.InstallDirectory, "guard-tray"));
    private readonly string _stage = Path.Combine(paths.InstallDirectory, ".tray-stage-" + Guid.NewGuid().ToString("N"));
    private readonly string _backup = Path.Combine(paths.InstallDirectory, ".tray-backup-" + Guid.NewGuid().ToString("N"));
    private bool _movedOriginal;
    private bool _published;
    private string? _expected;

    public bool NeedsUpdate()
    {
        RuntimeSafety.RequireLiveOperations();
        if (string.Equals(_source, _destination, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(Path.Combine(_source, "EgressController.GuardTray.exe")))
            throw new FileNotFoundException("发布包缺少守护托盘，已保留原托盘。请复制完整发布包。");
        _expected = PayloadDigest(_source);
        return !Directory.Exists(_destination) || _expected != PayloadDigest(_destination);
    }
    public void Stage()
    {
        RuntimeSafety.RequireLiveOperations();
        if (!GuardWindowsIdentity.Elevated) throw new UnauthorizedAccessException("同步新版守护托盘需要管理员权限，请以管理员身份重新打开主程序。");
        WindowsGuardPlatform.SecureDirectory(paths.InstallDirectory, paths.OwnerSid);
        WindowsGuardPlatform.CopyPayload(_source, _stage, paths.OwnerSid);
        if (_expected != PayloadDigest(_stage) || _expected != PayloadDigest(_source))
            throw new IOException("复制期间发布包发生变化，已取消托盘更新；原托盘保留。");
    }
    public void StopInstalledTray()
    {
        RuntimeSafety.RequireLiveOperations();
        // A process name never grants termination authority. Resolve the exact installed path
        // and creation time, then let WindowsProcessControl verify both again on its handle.
        var resolver = new WindowsProcessIdentityResolver(new ExecutablePathCanonicalizer());
        var control = new WindowsProcessControl();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? path;
                try { path = process.MainModule?.FileName; } catch { continue; }
                if (!string.Equals(path, paths.TrayExecutable, StringComparison.OrdinalIgnoreCase)) continue;
                var identity = resolver.Resolve((uint)process.Id);
                if (identity?.ExePathFinal is null) { if (!process.HasExited) throw new IOException("无法确认旧托盘实例，未替换文件。"); continue; }
                if (!string.Equals(identity.ExePathFinal, paths.TrayExecutable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("旧托盘的最终路径不匹配，未替换文件。");
                var result = control.Terminate(new(new((uint)process.Id, identity.StartTimeUtc), 0, identity.ExePathFinal));
                if (!result.Succeeded || !process.WaitForExit(5000))
                    throw new IOException("旧托盘未能退出，已保留原文件：" + result.Error);
            }
        }
    }
    public void Publish()
    {
        RuntimeSafety.RequireLiveOperations();
        if (Directory.Exists(_destination)) { Directory.Move(_destination, _backup); _movedOriginal = true; }
        Directory.Move(_stage, _destination);
        _published = true;
    }
    public void Restore()
    {
        RuntimeSafety.RequireLiveOperations();
        if (_movedOriginal && !_published && !Directory.Exists(_destination)) Directory.Move(_backup, _destination);
    }
    public void Cleanup()
    {
        RuntimeSafety.RequireLiveOperations();
        // Only these two generated sibling directories can be removed. Never remove the live payload.
        foreach (string generated in new[] { _stage, _backup })
        {
            string full = Path.GetFullPath(generated);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(paths.InstallDirectory), StringComparison.OrdinalIgnoreCase))
                throw new IOException("托盘临时目录越界。");
            // A failed rollback keeps the backup available for recovery.
            if (full == _backup && !_published) continue;
            try { if (Directory.Exists(full)) Directory.Delete(full, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    internal static string PayloadDigest(string root)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in Walk(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            digest.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, item).ToLowerInvariant() + "\0"));
            using var stream = File.OpenRead(item);
            digest.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }
    private static IEnumerable<string> Walk(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("托盘发布目录不能经过重解析点。");
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("托盘发布文件不能经过重解析点。");
            if ((attributes & FileAttributes.Directory) == 0) yield return path;
            else foreach (string child in Walk(path)) yield return child;
        }
    }
}
