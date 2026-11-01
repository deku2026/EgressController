using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.MacHelper;

public static class HelperInstallation
{
    public static MacHelperInstallation ReadInstalled(NativeMacPrivileges native)
    {
        var result = JsonSerializer.Deserialize(HostFiles.ReadOwned(native, MacHelperPaths.Installation, 0, 65536), MacHelperWireJson.Default.MacHelperInstallation)
            ?? throw new InvalidDataException("Missing helper installation identity.");
        Validate(result); return result;
    }
    public static void Validate(MacHelperInstallation value)
    {
        if (value.Version != 2 || value.XraySha256 != XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256
            || value.AuthorizedUserId == 0 || string.IsNullOrEmpty(value.AuthorizedGuiPath)
            || !Path.IsPathFullyQualified(value.AuthorizedGuiPath) || string.IsNullOrEmpty(value.UserStagingRoot) || !Path.IsPathFullyQualified(value.UserStagingRoot)
            || new[] { value.AuthorizedGuiSha256, value.XraySha256, value.HelperSha256, value.NativeLibrarySha256 }
                .Any(hash => hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit))) throw new InvalidDataException("Invalid installed helper identity.");
    }
    public static async Task InstallAsync(string planPath, NativeMacPrivileges native)
    {
        Program.WriteDiagnostic("stage=read-plan-metadata");
        var planOwner = native.ReadFileInfo(planPath);
        if (planOwner.IsSymbolicLink || !planOwner.IsRegular) throw new InvalidDataException("Invalid installer plan owner.");
        Program.WriteDiagnostic("stage=read-plan");
        var plan = JsonSerializer.Deserialize(HostFiles.ReadOwned(native, planPath, planOwner.UserId, 65536), MacHelperWireJson.Default.MacHelperInstallPlan)
            ?? throw new InvalidDataException("Missing installation plan.");
        Validate(plan.Installation);
        if ((planOwner.UserId != 0 && plan.Installation.AuthorizedUserId != planOwner.UserId) || plan.SourceHelperPath != Environment.ProcessPath)
            throw new InvalidDataException("The installer identity differs from the authorized payload.");
        Program.WriteDiagnostic("stage=verify-payloads");
        uint user = plan.Installation.AuthorizedUserId;
        uint guiOwner = native.ReadFileInfo(plan.Installation.AuthorizedGuiPath).UserId;
        if (guiOwner != 0 && guiOwner != user) throw new InvalidDataException("Unexpected GUI owner.");
        byte[] gui = HostFiles.ReadOwned(native, plan.Installation.AuthorizedGuiPath, guiOwner, 256 * 1024 * 1024);
        byte[] helper = HostFiles.ReadOwned(native, plan.SourceHelperPath, planOwner.UserId, 256 * 1024 * 1024);
        byte[] library = HostFiles.ReadOwned(native, plan.SourceNativeLibraryPath, planOwner.UserId, 64 * 1024 * 1024);
        byte[] core = HostFiles.ReadOwned(native, plan.SourceXrayPath, user, 128 * 1024 * 1024);
        if (!MacHelperRequestValidator.ContentMatches(gui, plan.Installation.AuthorizedGuiSha256)
            || !MacHelperRequestValidator.ContentMatches(helper, plan.Installation.HelperSha256)
            || !MacHelperRequestValidator.ContentMatches(library, plan.Installation.NativeLibrarySha256)
            || !MacHelperRequestValidator.ContentMatches(core, plan.Installation.XraySha256)) throw new InvalidDataException("The installation payload changed.");
        // Existing helper receives SIGTERM and restores its durable DNS journal before replacement.
        await LaunchctlAsync(["bootout", "system/" + MacHelperPaths.Label], allowMissing: true).ConfigureAwait(false);
        if (File.Exists(MacHelperPaths.Installation))
            await new NativeRuntimeEnvironment(native, ReadInstalled(native)).RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        Program.WriteDiagnostic("stage=prepare-directories");
        HostFiles.PrivateDirectory(native, MacHelperPaths.PrivateRoot);
        HostFiles.PrivateDirectory(native, "/Library/PrivilegedHelperTools", restrictToRoot: false);
        HostFiles.PrivateDirectory(native, "/Library/LaunchDaemons", restrictToRoot: false);
        string[] targets = [MacHelperEndpoint.Installed.ExpectedHelperPath, MacHelperPaths.NativeLibrary,
            MacHelperPaths.InstalledXray, MacHelperPaths.Installation, MacHelperPaths.LaunchDaemon];
        var previous = targets.ToDictionary(path => path, File.Exists, StringComparer.Ordinal);
        foreach (string path in targets) Backup(native, path);
        try
        {
            Program.WriteDiagnostic("stage=publish-payloads");
            HostFiles.AtomicWrite(native, MacHelperPaths.NativeLibrary, library, 0x1ed);
            HostFiles.AtomicWrite(native, MacHelperPaths.InstalledXray, core, 0x1c0);
            HostFiles.AtomicWrite(native, MacHelperEndpoint.Installed.ExpectedHelperPath, helper, 0x1ed);
            HostFiles.AtomicWrite(native, MacHelperPaths.Installation, JsonSerializer.SerializeToUtf8Bytes(plan.Installation, MacHelperWireJson.Default.MacHelperInstallation));
            string plist = """
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>app.egresscontroller.mac.helper</string>
<key>ProgramArguments</key><array><string>/Library/PrivilegedHelperTools/app.egresscontroller.mac.helper</string><string>--serve</string></array>
<key>RunAtLoad</key><true/><key>KeepAlive</key><true/><key>ThrottleInterval</key><integer>5</integer>
<key>ExitTimeOut</key><integer>30</integer><key>AbandonProcessGroup</key><false/>
<key>EnvironmentVariables</key><dict><key>PATH</key><string>/usr/bin:/bin:/usr/sbin:/sbin</string></dict>
</dict></plist>
""";
            HostFiles.AtomicWrite(native, MacHelperPaths.LaunchDaemon, Encoding.UTF8.GetBytes(plist), 0x1a4);
            await LaunchctlAsync(["bootstrap", "system", MacHelperPaths.LaunchDaemon], allowMissing: false).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            Program.WriteDiagnostic("stage=rollback cause=" + OperationDiagnostics.Describe(original));
            await LaunchctlAsync(["bootout", "system/" + MacHelperPaths.Label], allowMissing: true).ConfigureAwait(false);
            foreach (string path in targets)
            {
                if (previous[path] && File.Exists(path + ".previous")) File.Move(path + ".previous", path, true);
                else if (!previous[path] && File.Exists(path)) File.Delete(path);
                HostFiles.SyncDirectory(Path.GetDirectoryName(path)!);
            }
            if (previous[MacHelperPaths.LaunchDaemon]) await LaunchctlAsync(["bootstrap", "system", MacHelperPaths.LaunchDaemon], allowMissing: true).ConfigureAwait(false);
            throw;
        }
    }
    private static void Backup(NativeMacPrivileges native, string path)
    {
        if (!File.Exists(path)) return;
        var file = native.ReadFileInfo(path);
        if (file.UserId != 0 || file.IsSymbolicLink || !file.IsRegular) throw new InvalidDataException("Unexpected helper replacement target.");
        HostFiles.AtomicWrite(native, path + ".previous", HostFiles.ReadOwned(native, path, 0, 256 * 1024 * 1024), file.Mode & 0x1ff);
    }
    private static async Task LaunchctlAsync(string[] arguments, bool allowMissing)
    {
        Program.WriteDiagnostic("stage=launchctl-" + arguments[0]);
        var start = new ProcessStartInfo("/bin/launchctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("launchd command could not start.");
        Task<string> output = MacAdminAuthorization.ReadBoundedOutputAsync(child.StandardOutput), error = MacAdminAuthorization.ReadBoundedOutputAsync(child.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try { await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false); }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
        Program.WriteDiagnostic($"stage=launchctl-{arguments[0]} exit={child.ExitCode} allow-missing={allowMissing}; " + OperationDiagnostics.SanitizeOutput(output.Result + " " + error.Result));
        if (child.ExitCode != 0 && !allowMissing) throw new RuntimeFailureException("helper.install", $"launchctl-{arguments[0]} exit={child.ExitCode}");
    }
}
