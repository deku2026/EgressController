using System.Text.Json;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class OperationDiagnosticsTests
{
    [Fact]
    public void FailureKeepsTheJsonFieldAndErrorCodeWithoutExceptionSecrets()
    {
        var error = new JsonException("password=private-value https://private.invalid/token", "$.IsRegular", 0, 52);
        string result = OperationDiagnostics.Describe(error);
        Assert.Contains("JsonException", result);
        Assert.Contains("json-path=$.IsRegular", result);
        Assert.Contains("byte=52", result);
        Assert.DoesNotContain("private", result);
        Assert.Contains("code=helper.install", OperationDiagnostics.Describe(new RuntimeFailureException("helper.install", "launchctl-bootstrap exit=5")));
        string identity = OperationDiagnostics.Describe(new RuntimeFailureException("helper.identity", "operation=peer; native-status=-2; errno=1"));
        Assert.Contains("code=helper.identity", identity);
        Assert.Contains("operation=peer", identity);
        Assert.Contains("native-status=-2", identity);
        Assert.Contains("errno=1", identity);
    }

    [Fact]
    public void BrokenLoggingCannotReplaceTheOriginalOutcome()
    {
        var diagnostics = new OperationDiagnostics(new FailingLog());
        diagnostics.Record("start", "explicit request");
        diagnostics.Failure("install", new IOException("fixture failure"));
    }

    [Fact]
    public void InstallerOutputIsBoundedRedactedAndCannotForgeLogLines()
    {
        string result = OperationDiagnostics.SanitizeOutput(new string('x', 9000) + "\npassword=private-value token=secret-value https://private.invalid/token\u001b");
        Assert.True(result.Length < 8192);
        Assert.DoesNotContain("private-value", result);
        Assert.DoesNotContain("secret-value", result);
        Assert.DoesNotContain("private.invalid", result);
        Assert.DoesNotContain('\n', result);
        Assert.DoesNotContain('\u001b', result);
    }
    private sealed class FailingLog : ILifecycleLog { public void Append(LifecycleEvent entry) => throw new IOException("read-only disk"); }
}
