namespace EgressController.Launcher.Discovery;

public static class ApplicationDirectoryBoundary
{
    public static void RequireScoped(string root)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string[] shared = [Path.GetPathRoot(full) ?? "", Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)];
        if (shared.Any(path => path.Length > 0 && full.Equals(path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            || new[] { "Users", "Windows", "System32", "SysWOW64", "Common Files", "steamapps", "WeGameApps" }.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
            throw new IOException("请选择具体应用的安装目录，不能使用系统目录或整个游戏库：" + root);
    }
}
