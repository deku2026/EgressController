using EgressController.Mac;
using EgressController.Runtime;
using System.Diagnostics;

namespace EgressController.Mac.Tests;

public sealed class InstallerDiagnosticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task AppleScriptPreservesInstallerStderrAndExitStatusWithoutElevation(int exitCode)
    {
        if (!OperatingSystem.IsMacOS()) return;
        // The exact production wrapper runs a synthetic printf/exit only. No
        // installation command, administrator prompt, disk or network mutation.
        string script = $"set -eu; trap 'printf fixture-trap >&2' EXIT; printf fixture-stage >&2; exit {exitCode}";
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(MacAdminAuthorization.BuildAppleScript(script, administrator: false));
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        string captured = await output + await error;
        Assert.Contains("fixture-stage", captured);
        Assert.Contains("fixture-trap", captured);
        Assert.Equal(exitCode == 0, child.ExitCode == 0);
        if (exitCode != 0) Assert.Contains("(7)", captured);
    }

    [Fact]
    public async Task IdentityDiagnosisSurvivesReadinessCancellationAndClearsOnRecovery()
    {
        var connection = new ChangingConnection();
        var log = new BoundedLifecycleLog();
        await using var helper = new MacHelperClient(connection, log: log);
        connection.Failure = new RuntimeFailureException("helper.identity", "operation=peer; native-status=-2; errno=1");
        Assert.False((await helper.InspectAsync(TestContext.Current.CancellationToken)).Installed);
        Assert.Equal("helper.identity", helper.TransportFailureCode);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => helper.InspectAsync(canceled.Token));
        Assert.Equal("helper.identity", helper.TransportFailureCode);
        connection.Failure = null;
        Assert.True((await helper.InspectAsync(TestContext.Current.CancellationToken)).Installed);
        Assert.Null(helper.TransportFailureCode);
    }
    [Fact]
    public void PureNativeFileInfoRoundTripsThroughTheProductionJsonContract()
    {
        if (OperatingSystem.IsMacOS()) MacNativeContractChecks.VerifyFileInfo();
    }

    [Theory]
    [InlineData("execution error: User canceled. (-128)", "authorization.denied")]
    [InlineData("execution error: denied (-60007)", "authorization.denied")]
    [InlineData("EGRESS_INSTALL stage=read-plan JsonException json-path=$.IsRegular", "helper.install-data")]
    [InlineData("Abort trap: 6 (134)", "helper.install-crashed")]
    [InlineData("launchctl bootstrap: Input/output error (5)", "helper.install")]
    [InlineData("failed to copy /example/file-128", "helper.install")]
    public void CancellationCrashAndInstallerErrorsRemainDistinct(string output, string code)
        => Assert.Equal(code, MacAdminAuthorization.ClassifyFailure(output));

    [Fact]
    public async Task CapturedOutputIsDrainedButKeepsOnlyABoundedTail()
    {
        string result = await MacAdminAuthorization.ReadBoundedOutputAsync(new StringReader(new string('x', 100000) + "stage=read-plan JsonException"));
        Assert.True(result.Length < 16420);
        Assert.StartsWith("[earlier output truncated]", result);
        Assert.EndsWith("stage=read-plan JsonException", result);
    }

    [Fact]
    public async Task BrokenDiagnosticsCannotRetainHealthyHelperStatus()
    {
        var connection = new ChangingConnection();
        await using var helper = new MacHelperClient(connection, log: new BrokenLog());
        Assert.True((await helper.InspectAsync(TestContext.Current.CancellationToken)).TunTakenOver);
        connection.Fail = true;
        Assert.False((await helper.InspectAsync(TestContext.Current.CancellationToken)).TunTakenOver);
        connection.Fail = false;
        Assert.True((await helper.InspectAsync(TestContext.Current.CancellationToken)).TunTakenOver);
    }
    private sealed class BrokenLog : ILifecycleLog { public void Append(LifecycleEvent entry) => throw new IOException("full disk"); }
    private sealed class ChangingConnection : IMacHelperConnection
    {
        public bool Fail;
        public Exception? Failure;
        public Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("injected transport failure");
            if (Failure is not null) throw Failure;
            return Task.FromResult(new MacHelperWireReply(request.SessionId, request.Sequence, true, "ok", "",
                new(true, true, true, false, 42, "fixture", null, DateTimeOffset.UtcNow)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
