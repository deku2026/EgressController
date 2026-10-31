// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EgressController.Runtime;

namespace EgressController.Mac;

public interface IMacAdminAuthorization
{
    Task AuthorizeInstallAsync(string script, CancellationToken cancellationToken);
}

public sealed class MacAdminAuthorization(LiveOperationGate gate) : IMacAdminAuthorization
{
    public async Task AuthorizeInstallAsync(string script, CancellationToken cancellationToken)
    {
        gate.Require("request administrator authorization to install the Egress helper"); cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add("do shell script " + AppleScriptLiteral(script) + " with administrator privileges with prompt \"Egress 需要安装经过校验的网络辅助服务。\"");
        using var process = Process.Start(start) ?? throw new IOException("Unable to request administrator authorization.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        // Once authorization starts, finish the atomic installer; caller cancellation cannot abandon half an installation.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new RuntimeFailureException(error.Result.Contains("-128", StringComparison.Ordinal)
            || error.Result.Contains("-60007", StringComparison.Ordinal) ? "authorization.denied" : "helper.install");
    }
    private static string AppleScriptLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}

/// <summary>Only explicit UI installation/apply actions invoke this API. Startup never requests authorization.</summary>
public sealed class MacHelperInstaller(LiveOperationGate gate, IMacAdminAuthorization? authorization = null)
{
    public async Task InstallAsync(MacHelperInstallPlan plan, CancellationToken cancellationToken = default)
    {
        gate.Require("install privileged helper with explicit administrator authorization");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ValidatePlan(plan);
        var native = new NativeMacPrivileges(gate);
        if (plan.Installation.AuthorizedUserId != native.GetUserId()) throw new UnauthorizedAccessException("The helper installation belongs to another user.");
        native.VerifyProcessCode(Environment.ProcessId, plan.Installation.AuthorizedGuiPath);
        await VerifyAsync(plan.SourceHelperPath, plan.Installation.HelperSha256, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(plan.SourceNativeLibraryPath, plan.Installation.NativeLibrarySha256, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(plan.SourceXrayPath, plan.Installation.XraySha256, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(plan.Installation.AuthorizedGuiPath, plan.Installation.AuthorizedGuiSha256, cancellationToken).ConfigureAwait(false);
        string root = Path.Combine(plan.Installation.UserStagingRoot, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string manifestPath = Path.Combine(root, "install-plan.json");
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(plan, MacHelperWireJson.Default.MacHelperInstallPlan);
        try
        {
            await using (var stream = new FileStream(manifestPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Options = FileOptions.WriteThrough, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { await stream.WriteAsync(manifest, cancellationToken).ConfigureAwait(false); stream.Flush(true); }
            string script = BuildAuthorizationScript(plan, manifestPath, Convert.ToHexStringLower(SHA256.HashData(manifest)));
            await (authorization ?? new MacAdminAuthorization(gate)).AuthorizeInstallAsync(script, cancellationToken).ConfigureAwait(false);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public static void ValidatePlan(MacHelperInstallPlan plan)
    {
        if (plan is null || plan.Installation is null) throw new InvalidDataException("Missing helper installation plan.");
        var identity = plan.Installation;
        if (identity.Version != 2 || identity.AuthorizedUserId == 0 || Path.GetFileName(identity.AuthorizedGuiPath) != "EgressController.Desktop"
            || new[] { plan.SourceHelperPath, plan.SourceNativeLibraryPath, plan.SourceXrayPath, identity.AuthorizedGuiPath, identity.UserStagingRoot }.Any(path => !MacPath.IsAbsolute(path) || MacPath.HasTraversal(path))
            || new[] { identity.AuthorizedGuiSha256, identity.XraySha256, identity.HelperSha256, identity.NativeLibrarySha256 }.Any(hash => hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("The helper installation plan is not a bounded explicit application/core identity.");
    }
    public static string BuildAuthorizationScript(MacHelperInstallPlan plan, string manifestPath, string manifestSha256)
    {
        ValidatePlan(plan);
        if (!MacPath.IsAbsolute(manifestPath) || MacPath.HasTraversal(manifestPath) || manifestSha256 is not { Length: 64 } || !manifestSha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid installer manifest identity.");
        static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        static string CopyAndVerify(string source, string target, string hash, string mode) =>
            "/usr/bin/install -o root -g wheel -m " + mode + " " + Quote(source) + " " + target + "; "
            + "[ \"$(/usr/bin/shasum -a 256 " + target + " | /usr/bin/cut -d ' ' -f 1)\" = " + Quote(hash.ToLowerInvariant()) + " ]; ";
        return "set -eu; [ ! -L /Library/PrivilegedHelperTools ]; "
            + "if [ ! -e /Library/PrivilegedHelperTools ]; then /usr/bin/install -d -o root -g wheel -m 755 /Library/PrivilegedHelperTools; fi; "
            + "[ -d /Library/PrivilegedHelperTools ]; [ \"$(/usr/bin/stat -f %u /Library/PrivilegedHelperTools)\" = 0 ]; "
            + "egress_install_parent_mode=$(/usr/bin/stat -f %Lp /Library/PrivilegedHelperTools); [ $((0$egress_install_parent_mode & 022)) -eq 0 ]; "
            + "egress_install_stage=$(/usr/bin/mktemp -d /Library/PrivilegedHelperTools/.egress-install.XXXXXXXX); "
            + "trap '/bin/rm -rf \"$egress_install_stage\"' EXIT; /bin/chmod 700 \"$egress_install_stage\"; "
            + CopyAndVerify(plan.SourceHelperPath, "\"$egress_install_stage/helper\"", plan.Installation.HelperSha256, "700")
            + CopyAndVerify(plan.SourceNativeLibraryPath, "\"$egress_install_stage/libEgressMacNative.dylib\"", plan.Installation.NativeLibrarySha256, "600")
            + CopyAndVerify(manifestPath, "\"$egress_install_stage/install-plan.json\"", manifestSha256, "600")
            + "/usr/bin/plutil -replace SourceHelperPath -string \"$egress_install_stage/helper\" \"$egress_install_stage/install-plan.json\"; "
            + "/usr/bin/plutil -replace SourceNativeLibraryPath -string \"$egress_install_stage/libEgressMacNative.dylib\" \"$egress_install_stage/install-plan.json\"; "
            + "/usr/bin/plutil -convert json \"$egress_install_stage/install-plan.json\"; "
            + "\"$egress_install_stage/helper\" --install \"$egress_install_stage/install-plan.json\"";
    }
    private static async Task VerifyAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("An installation artifact did not match its pinned digest.");
    }
}
