using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.MacHelper;

public static class Program
{
    private static OperationDiagnostics? _serviceDiagnostics;
    public static async Task<int> Main(string[] args)
    {
        // This branch precedes gate, uid, disk, socket and native initialization.
        if (args.SequenceEqual(["--mock-smoke"]))
        {
            var request = new MacHelperWireRequest(2, Guid.NewGuid(), 1, MacHelperCommand.Inspect);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, MacHelperWireJson.Default.MacHelperWireRequest);
            if (JsonSerializer.Deserialize(bytes, MacHelperWireJson.Default.MacHelperWireRequest) != request) return 1;
            if (OperatingSystem.IsMacOS()) { MacNativeContractChecks.VerifyFileInfo(); MacNativeContractChecks.VerifyInterfaceInfo(); }
            Console.WriteLine("Helper mock smoke passed; pure native file-info/interface AOT JSON contracts verified; no live initialization."); return 0;
        }
        try { return await RunProductionAsync(args).ConfigureAwait(false); }
        catch (Exception error)
        {
            // Do not abort on an installation failure and lose its cause to the macOS crash reporter.
            WriteDiagnostic("helper.failed " + OperationDiagnostics.Describe(error));
            return 1;
        }
    }
    internal static void WriteDiagnostic(string detail)
    {
        try { Console.Error.WriteLine("EGRESS_HELPER " + OperationDiagnostics.SanitizeOutput(detail)); } catch (Exception) { }
        _serviceDiagnostics?.Record("helper.service", detail);
    }
    private static async Task<int> RunProductionAsync(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("EGRESS_MOCK_ONLY") == "1")
            throw new InvalidOperationException("Production helper is prohibited in mock validation.");
        var privileges = new NativeMacPrivileges(LiveOperationGate.ForExplicitLiveUse());
        if (privileges.GetEffectiveUserId() != 0) throw new UnauthorizedAccessException("The helper must be started by authorized installation or launchd.");
        if (args is ["--install", var plan]) { await HelperInstallation.InstallAsync(plan, privileges).ConfigureAwait(false); return 0; }
        if (!args.SequenceEqual(["--serve"])) throw new ArgumentException("Expected --serve or --install <plan>.");
        _serviceDiagnostics = new OperationDiagnostics(new HelperServiceLog(privileges));
        WriteDiagnostic($"stage=service-started pid={Environment.ProcessId} euid=0");
        var installation = HelperInstallation.ReadInstalled(privileges);
        var environment = new NativeRuntimeEnvironment(privileges, installation);
        WriteDiagnostic("stage=startup-recovery");
        await environment.RestoreAsync(CancellationToken.None).ConfigureAwait(false); // Recover durable journal after a crash/reboot.
        WriteDiagnostic("stage=startup-recovery-complete");
        var controller = new RuntimeController(environment);
        using var stopping = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stopping.Cancel(); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stopping.Cancel(); });
        string socketPath = MacHelperEndpoint.Installed.SocketPath;
        string parent = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(parent); privileges.SetOwnerAndMode(parent, 0, 0, 0x1ed); // 0755; clients cannot replace the endpoint.
        if (File.Exists(socketPath))
        {
            var stale = privileges.ReadFileInfo(socketPath);
            if (!stale.IsSocket || stale.UserId != installation.AuthorizedUserId) throw new IOException("Unexpected existing helper endpoint.");
            File.Delete(socketPath);
        }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        privileges.SetOwnerAndMode(socketPath, installation.AuthorizedUserId, installation.AuthorizedGroupId, 0x180); // 0600.
        listener.Listen(4);
        WriteDiagnostic("stage=endpoint-listening");
        string? lastPeerFailure = null;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                using Socket client = await listener.AcceptAsync(stopping.Token).ConfigureAwait(false);
                MacPeerIdentity peer;
                try { peer = privileges.ReadPeer(client.Handle.ToInt32()); await MacPeerSecurity.VerifyAsync(privileges, peer, installation, stopping.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
                {
                    string failure = OperationDiagnostics.Describe(error);
                    if (failure != lastPeerFailure) { lastPeerFailure = failure; WriteDiagnostic("stage=peer-rejected " + failure); }
                    continue;
                }
                lastPeerFailure = null;
                WriteDiagnostic($"stage=peer-authenticated pid={peer.ProcessId} uid={peer.UserId}");
                using var stream = new NetworkStream(client, ownsSocket: false);
                Guid session = Guid.Empty; long sequence = 0;
                try
                {
                    while (!stopping.IsCancellationRequested)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(12));
                        byte[] payload = await ReadFrameAsync(stream, deadline.Token).ConfigureAwait(false);
                        var request = JsonSerializer.Deserialize(payload, MacHelperWireJson.Default.MacHelperWireRequest)
                            ?? throw new InvalidDataException("Empty helper request.");
                        if (session == Guid.Empty) session = request.SessionId;
                        if (request.Version != 2 || session == Guid.Empty || request.SessionId != session || request.Sequence <= sequence
                            || !Enum.IsDefined(request.Command) || (request.Apply is not null && request.Command is not (MacHelperCommand.Validate or MacHelperCommand.Apply)))
                            throw new InvalidDataException("Invalid helper session or sequence.");
                        var current = privileges.ReadProcess(peer.ProcessId);
                        if (current != peer) throw new UnauthorizedAccessException("The authenticated GUI instance changed.");
                        await MacPeerSecurity.VerifyAsync(privileges, current, installation, stopping.Token).ConfigureAwait(false);
                        sequence = request.Sequence;
                        await controller.CheckCoreAsync(stopping.Token).ConfigureAwait(false);
                        if (request.Command is not (MacHelperCommand.Inspect or MacHelperCommand.Heartbeat)) WriteDiagnostic("stage=request command=" + request.Command);
                        var reply = await controller.ExecuteAsync(request, stopping.Token).ConfigureAwait(false);
                        if (!reply.Succeeded || request.Command is not (MacHelperCommand.Inspect or MacHelperCommand.Heartbeat))
                            WriteDiagnostic($"stage=reply command={request.Command} succeeded={reply.Succeeded} code={reply.Code} running={reply.Status.Running} tun={reply.Status.TunTakenOver}");
                        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(reply, MacHelperWireJson.Default.MacHelperWireReply), stopping.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or SocketException or OperationCanceledException or JsonException or UnauthorizedAccessException or ArgumentException)
                { WriteDiagnostic("stage=session-ended " + OperationDiagnostics.Describe(error)); }
                finally
                {
                    WriteDiagnostic("stage=session-recovery");
                    await controller.RecoverAsync().ConfigureAwait(false);
                    WriteDiagnostic("stage=session-recovery-complete");
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            WriteDiagnostic("stage=service-stopping");
            await controller.RecoverAsync().ConfigureAwait(false); File.Delete(socketPath);
            WriteDiagnostic("stage=service-stopped recovery=complete");
        }
        return 0;
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MacHelperRequestValidator.MaximumFrameBytes) throw new InvalidDataException("Invalid helper frame size.");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false); return body;
    }
    public static async Task WriteFrameAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        if (body.Length is 0 or > MacHelperRequestValidator.MaximumFrameBytes) throw new InvalidDataException("Invalid helper frame size.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false); await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
