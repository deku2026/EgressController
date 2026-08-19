using System.Runtime.InteropServices;
using System.Security.Principal;

const uint RpcCAuthnDefault = 0xFFFFFFFF;

Console.WriteLine("EgressController.Probe.ConnectionPolicy");
Console.WriteLine($"os={Environment.OSVersion.VersionString}");
Console.WriteLine($"user={Environment.UserName}");
Console.WriteLine($"elevated={new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)}");

uint openResult = 0;
nint engine = 0;
try
{
    openResult = FwpmEngineOpen0(null, RpcCAuthnDefault, 0, 0, out engine);
    Console.WriteLine($"FwpmEngineOpen0=0x{openResult:X8}");
}
catch (DllNotFoundException ex)
{
    Console.WriteLine($"FwpmEngineOpen0=DLL_NOT_FOUND:{ex.Message}");
}
finally
{
    if (engine != 0)
        _ = FwpmEngineClose0(engine);
}

if (openResult != 0)
{
    Console.WriteLine("CONCLUSION=C");
    Console.WriteLine("reason=standard-user filter-engine access is unavailable; raw backend is disabled.");
}
else
{
    Console.WriteLine("CONCLUSION=B");
    Console.WriteLine("reason=FwpmConnectionPolicyAdd0 is app/user/package scoped, not PID/start-time scoped; an executable-path policy would affect both managed and ordinary instances of the same EXE.");
}

Console.WriteLine("policy_mutation=none");
Console.WriteLine("cleanup=engine session closed; no persistent policy was added");

[DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
static extern uint FwpmEngineOpen0(
    string? serverName,
    uint authnService,
    nint authIdentity,
    nint session,
    out nint engineHandle);

[DllImport("fwpuclnt.dll")]
static extern uint FwpmEngineClose0(nint engineHandle);
