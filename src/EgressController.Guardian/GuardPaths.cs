using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace EgressController.Guardian;

public sealed record GuardPaths(string OwnerSid, string ServiceName, string PipeName, string DataDirectory, string InstallDirectory)
{
    public static GuardPaths Current => ForSid(WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法读取当前 Windows 用户。"));
    public static GuardPaths ForSid(string sid)
    {
        _ = new SecurityIdentifier(sid);
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
        return new(sid, "EgressController.Guard." + suffix, "EgressController.Guard." + suffix,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EgressController", "Guard", suffix),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EgressController", "Guard", suffix));
    }
    public string ServiceExecutable => Path.Combine(InstallDirectory, "guard-service", "EgressController.GuardService.exe");
    public string ServiceCommandLine => $"\"{ServiceExecutable}\" --owner {OwnerSid}";
    public string TrayExecutable => Path.Combine(InstallDirectory, "guard-tray", "EgressController.GuardTray.exe");
    public string RegistrationFile => Path.Combine(DataDirectory, "registration.json");
}

public sealed class GuardRegistrationStore(string file) : IGuardRegistrationStore
{
    public GuardRegistration? Load()
    {
        var result = GuardStateFile.Read(file, GuardJson.Default.GuardRegistration);
        if (result is null) return null;
        if (result.Schema != 1 || string.IsNullOrWhiteSpace(result.OwnerSid)
            || !Path.IsPathFullyQualified(result.MainExecutable) || !Path.IsPathFullyQualified(result.InstallDirectory))
            throw new InvalidDataException("不支持或损坏的守护安装记录。");
        return result;
    }
    public void Save(GuardRegistration value) => GuardStateFile.Write(file, value, GuardJson.Default.GuardRegistration);
}
