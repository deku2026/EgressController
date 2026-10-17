using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using EgressController.Core.Protection;
using Microsoft.Win32;

namespace EgressController.Guardian;

public sealed class WindowsGuardPlatform(GuardPaths paths, string sourceDirectory) : IGuardServicePlatform
{
    private readonly WindowsGuardServiceControl _service = new(paths);
    public GuardServiceInfo Query() => _service.Query();
    public void Install(GuardRegistration registration, GuardPolicy policy)
    {
        RuntimeSafety.RequireLiveOperations();
        if (!GuardWindowsIdentity.Elevated) throw new UnauthorizedAccessException("安装守护服务需要管理员权限。");
        if (_service.Query().State != GuardServiceState.Missing)
            throw new InvalidOperationException("服务已经存在。如需重新安装，请先卸载服务；保护名单会保留。");
        // Validate both payloads before creating a service or changing machine startup.
        foreach (var component in new[] { ("guard-service", "EgressController.GuardService.exe"), ("guard-tray", "EgressController.GuardTray.exe") })
            if (!File.Exists(Path.Combine(sourceDirectory, component.Item1, component.Item2)))
                throw new FileNotFoundException("发布包缺少 NativeAOT 守护组件，请使用完整发布包：" + component.Item2);
        SecureDirectory(paths.DataDirectory, paths.OwnerSid);
        SecureDirectory(paths.InstallDirectory, paths.OwnerSid);
        foreach (string component in new[] { "guard-service", "guard-tray" })
            CopyPayload(Path.Combine(sourceDirectory, component), Path.Combine(paths.InstallDirectory, component), paths.OwnerSid);
        // Bootstrap is the only non-service writer, before the service exists.
        new GuardStore(paths.DataDirectory).SavePolicy(policy);
        new GuardRegistrationStore(paths.RegistrationFile).Save(registration);
        _service.Install(registration.AutoStart);
    }
    public void Start() => _service.Start();
    public void Stop(bool force) => _service.Stop(force);
    public void SetAutoStart(bool enabled) => _service.SetAutoStart(enabled);
    public void Uninstall() => _service.Delete();
    public void ConfigureTray(bool enabled)
    {
        RuntimeSafety.RequireLiveOperations();
        using var key = Registry.Users.CreateSubKey(paths.OwnerSid + @"\Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new InvalidOperationException("无法更新托盘登录启动设置。");
        if (enabled) key.SetValue(paths.ServiceName, $"\"{paths.TrayExecutable}\" --owner {paths.OwnerSid}");
        else key.DeleteValue(paths.ServiceName, false);
    }
    public void StartTray()
    {
        RuntimeSafety.RequireLiveOperations();
        if (!File.Exists(paths.TrayExecutable)) throw new FileNotFoundException("守护托盘程序缺失。");
        var info = new ProcessStartInfo(paths.TrayExecutable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--owner"); info.ArgumentList.Add(paths.OwnerSid);
        using var process = System.Diagnostics.Process.Start(info);
    }
    private static void SecureDirectory(string directory, string owner)
    {
        // Reject junctions/symlinks before any elevated copy/ACL change.
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("守护安装/数据目录不能经过重解析点：" + parent.FullName);
        Directory.CreateDirectory(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(owner), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
    private static void CopyPayload(string source, string destination, string owner)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("守护发布目录不能是重解析点。");
        SecureDirectory(destination, owner);
        foreach (string file in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("守护发布文件不能是重解析点。");
            string target = Path.Combine(destination, Path.GetFileName(file));
            if ((attributes & FileAttributes.Directory) != 0) { CopyPayload(file, target, owner); continue; }
            // Replace directory entries instead of overwriting a potentially pre-existing
            // hard link. New files inherit the secured destination's ACL.
            string temporary = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".tmp");
            try { File.Copy(file, temporary, false); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
