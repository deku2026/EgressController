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

public sealed class MacAdminAuthorization(LiveOperationGate gate, ILifecycleLog? log = null) : IMacAdminAuthorization
{
    public async Task AuthorizeInstallAsync(string script, CancellationToken cancellationToken)
    {
        gate.Require("request administrator authorization to install the Egress helper"); cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(BuildAppleScript(script));
        var diagnostics = new OperationDiagnostics(log);
        diagnostics.Record("helper.authorization.requested", "macOS administrator prompt; GUI remains unprivileged");
        using var process = Process.Start(start) ?? throw new IOException("Unable to request administrator authorization.");
        Task<string> output = ReadBoundedOutputAsync(process.StandardOutput), error = ReadBoundedOutputAsync(process.StandardError);
        // Once authorization starts, finish the atomic installer; caller cancellation cannot abandon half an installation.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false);
        diagnostics.Record("helper.authorization.finished", $"osascript-exit={process.ExitCode}");
        // Redact before chunking, so a credential assignment cannot straddle log records.
        foreach (string captured in new[] { output.Result, error.Result })
        {
            string safeOutput = OperationDiagnostics.SanitizeOutput(captured);
            for (int offset = 0; offset < safeOutput.Length; offset += 900)
                diagnostics.Record("helper.install.output", safeOutput.Substring(offset, Math.Min(900, safeOutput.Length - offset)));
        }
        if (process.ExitCode != 0) throw new RuntimeFailureException(ClassifyFailure(error.Result), $"osascript-exit={process.ExitCode}; see helper.install.output");
    }
    public static string ClassifyFailure(string error) => error.Contains("(-128)", StringComparison.Ordinal) || error.Contains("(-60007)", StringComparison.Ordinal)
        ? "authorization.denied" : error.Contains("JsonException", StringComparison.Ordinal) ? "helper.install-data"
        : error.Contains("Abort trap", StringComparison.Ordinal) || error.Contains("SIGABRT", StringComparison.Ordinal) ? "helper.install-crashed" : "helper.install";
    public static string BuildAppleScript(string script, bool administrator = true)
        // AppleScript discards stderr on success. Merge at shell entry so both
        // success traces and failure traces (including the EXIT trap) survive.
        => "do shell script " + AppleScriptLiteral("exec 2>&1; " + script)
            + (administrator ? " with administrator privileges with prompt \"Egress 需要安装经过校验的网络辅助服务。\"" : "");
    public static async Task<string> ReadBoundedOutputAsync(TextReader reader)
    {
        const int limit = 16384;
        var tail = new System.Text.StringBuilder(); char[] buffer = new char[2048]; int count; bool truncated = false;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > limit) { tail.Remove(0, tail.Length - limit); truncated = true; }
        }
        return (truncated ? "[earlier output truncated]\n" : "") + tail;
    }
    private static string AppleScriptLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}

/// <summary>Only explicit UI installation/apply actions invoke this API. Startup never requests authorization.</summary>
public sealed class MacHelperInstaller(LiveOperationGate gate, IMacAdminAuthorization? authorization = null, ILifecycleLog? log = null)
{
    public async Task InstallAsync(MacHelperInstallPlan plan, CancellationToken cancellationToken = default)
    {
        gate.Require("install privileged helper with explicit administrator authorization");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var diagnostics = new OperationDiagnostics(log);
        diagnostics.Record("helper.install.preflight", "validate explicit GUI/helper/native/core identity and pinned hashes");
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
            await (authorization ?? new MacAdminAuthorization(gate, log)).AuthorizeInstallAsync(script, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { diagnostics.Failure("helper.install", error); throw; }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (Exception error) { diagnostics.Failure("helper.install.cleanup", error); }
        }
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
        return "set -eu; egress_install_step=authorized; egress_install_stage=''; "
            + "trap 'egress_install_result=$?; echo \"EGRESS_INSTALL stage=$egress_install_step exit=$egress_install_result\" >&2; "
            + "if [ -n \"$egress_install_stage\" ]; then /bin/rm -rf \"$egress_install_stage\"; fi; exit \"$egress_install_result\"' EXIT; "
            + "echo 'EGRESS_INSTALL stage=authorized' >&2; egress_install_step=secure-parent; [ ! -L /Library/PrivilegedHelperTools ]; "
            + "if [ ! -e /Library/PrivilegedHelperTools ]; then /usr/bin/install -d -o root -g wheel -m 755 /Library/PrivilegedHelperTools; fi; "
            + "[ -d /Library/PrivilegedHelperTools ]; [ \"$(/usr/bin/stat -f %u /Library/PrivilegedHelperTools)\" = 0 ]; "
            + "egress_install_parent_mode=$(/usr/bin/stat -f %Lp /Library/PrivilegedHelperTools); [ $((0$egress_install_parent_mode & 022)) -eq 0 ]; "
            + "egress_install_stage=$(/usr/bin/mktemp -d /Library/PrivilegedHelperTools/.egress-install.XXXXXXXX); "
            + "/bin/chmod 700 \"$egress_install_stage\"; egress_install_step=copy-helper; "
            + CopyAndVerify(plan.SourceHelperPath, "\"$egress_install_stage/helper\"", plan.Installation.HelperSha256, "700")
            + "egress_install_step=copy-native; "
            + CopyAndVerify(plan.SourceNativeLibraryPath, "\"$egress_install_stage/libEgressMacNative.dylib\"", plan.Installation.NativeLibrarySha256, "600")
            + "egress_install_step=copy-manifest; "
            + CopyAndVerify(manifestPath, "\"$egress_install_stage/install-plan.json\"", manifestSha256, "600")
            + "egress_install_step=rewrite-manifest; "
            + "/usr/bin/plutil -replace SourceHelperPath -string \"$egress_install_stage/helper\" \"$egress_install_stage/install-plan.json\"; "
            + "/usr/bin/plutil -replace SourceNativeLibraryPath -string \"$egress_install_stage/libEgressMacNative.dylib\" \"$egress_install_stage/install-plan.json\"; "
            + "/usr/bin/plutil -convert json \"$egress_install_stage/install-plan.json\"; "
            + "egress_install_step=run-installer; echo 'EGRESS_INSTALL stage=run-installer' >&2; "
            + "\"$egress_install_stage/helper\" --install \"$egress_install_stage/install-plan.json\"";
    }
    private static async Task VerifyAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("An installation artifact did not match its pinned digest.");
    }
}
