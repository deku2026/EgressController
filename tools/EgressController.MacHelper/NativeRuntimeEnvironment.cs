using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.MacHelper;

public sealed record RecoveryJournal(MacDnsRecoverySnapshot Dns, int CorePid = 0, long CoreStartedAtMicroseconds = 0);
[JsonSerializable(typeof(RecoveryJournal))]
internal partial class HelperHostJson : JsonSerializerContext;

internal sealed class NativeRuntimeEnvironment(NativeMacPrivileges native, MacHelperInstallation installation) : IPrivilegedRuntimeEnvironment
{
    private Process? _core;
    private MacHelperApplyPlan? _active;
    private string? _tunName, _tunCidr, _failure;
    private bool _dnsRestored = true;
    private XrayConnectionObserver? _observer;
    private readonly OperationDiagnostics _diagnostics = new(new HelperServiceLog(native));
    private readonly CoreStartupDiagnosis _startup = new();
    private static string ControlSocket => Path.Combine(MacHelperPaths.PrivateRoot, "routing.sock");
    private static string JournalPath => Path.Combine(MacHelperPaths.PrivateRoot, "recovery.json");

    public async Task ValidateAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken)
    {
        if (plan.GenerationId is not { Length: 64 } || !plan.GenerationId.All(char.IsAsciiHexDigitLower)
            || !Guid.TryParse(plan.HomeServiceId, out _) || !Guid.TryParse(plan.NonHomeServiceId, out _) || plan.HomeServiceId == plan.NonHomeServiceId
            || plan.XraySha256 != installation.XraySha256 || installation.XraySha256 != XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256
            || string.IsNullOrEmpty(plan.GenerationDirectory) || !Path.IsPathFullyQualified(plan.GenerationDirectory)
            || !Path.GetFullPath(plan.GenerationDirectory).StartsWith(installation.UserStagingRoot.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new InvalidDataException("The requested generation or core identity is invalid.");
        byte[] executable = HostFiles.ReadOwned(native, MacHelperPaths.InstalledXray, 0, 128 * 1024 * 1024);
        if (!MacHelperRequestValidator.ContentMatches(executable, installation.XraySha256)) throw new InvalidDataException("Installed core identity changed.");
        byte[] manifestBytes = HostFiles.ReadOwned(native, Path.Combine(plan.GenerationDirectory, "runtime-manifest.json"), installation.AuthorizedUserId, 1024 * 1024);
        if (!MacHelperRequestValidator.ContentMatches(manifestBytes, plan.ManifestSha256)) throw new InvalidDataException("The runtime manifest changed.");
        var manifest = JsonSerializer.Deserialize(manifestBytes, MacHelperWireJson.Default.MacRuntimeManifest)
            ?? throw new InvalidDataException("Missing runtime manifest.");
        if (manifest.GenerationId != plan.GenerationId || manifest.Files is null || manifest.Files.Count is < 1 or > 256 || !manifest.Files.ContainsKey("xray.json")
            || manifest.Files.Keys.Any(name => !ConfigurationCoordinator.SafeArtifactName(name) || name is "runtime-manifest.json" or "recovery.json" or "core.runtime.json"))
            throw new InvalidDataException("Invalid runtime manifest.");
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal); long total = 0;
        foreach (var (name, hash) in manifest.Files)
        {
            byte[] bytes = HostFiles.ReadOwned(native, Path.Combine(plan.GenerationDirectory, name), installation.AuthorizedUserId, 64 * 1024 * 1024);
            if ((total += bytes.Length) > 256 * 1024 * 1024 || !MacHelperRequestValidator.ContentMatches(bytes, hash)) throw new InvalidDataException("Runtime contents changed or exceeded limits.");
            content.Add(name, bytes);
        }
        var interfaces = new NativeMacPlatform(LiveOperationGate.ForExplicitLiveUse()).ReadInterfaces();
        var selected = MacRuntimeInterfaces.Resolve(interfaces, plan.HomeServiceId, plan.NonHomeServiceId);
        var validation = MacHelperConfigurationValidator.Validate(content["xray.json"], selected.Home, selected.NonHome);
        if (!validation.IsValid || RuntimeAssetValidator.Validate(System.Text.Encoding.UTF8.GetString(content["xray.json"]), content)
            .Any(diagnostic => diagnostic.Severity == EgressController.Policy.DiagnosticSeverity.Error)) throw new InvalidDataException("Runtime configuration failed the privilege allow-list.");
        string root = Path.Combine(MacHelperPaths.PrivateRoot, "generations"); HostFiles.PrivateDirectory(native, root);
        string target = Path.Combine(root, plan.GenerationId);
        if (!Directory.Exists(target))
        {
            string staging = Path.Combine(root, ".preparing-" + Guid.NewGuid().ToString("N")); HostFiles.PrivateDirectory(native, staging);
            try
            {
                foreach (var (name, bytes) in content) HostFiles.AtomicWrite(native, Path.Combine(staging, name), bytes);
                HostFiles.AtomicWrite(native, Path.Combine(staging, "runtime-manifest.json"), manifestBytes);
                Directory.Move(staging, target); HostFiles.SyncDirectory(root);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        // Every reused root-owned generation is rechecked, not trusted merely because its directory exists.
        foreach (var (name, hash) in manifest.Files)
            if (!MacHelperRequestValidator.ContentMatches(HostFiles.ReadOwned(native, Path.Combine(target, name), 0, 64 * 1024 * 1024), hash))
                throw new InvalidDataException("The staged runtime failed its integrity check.");
        using var test = StartCore(target, testOnly: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { await test.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new RuntimeFailureException("core.validation-timeout"); }
        finally { if (!test.HasExited) { test.Kill(true); await test.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
        if (test.ExitCode != 0) throw new RuntimeFailureException("core.configuration");
    }

    public async Task ApplyAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(MacHelperPaths.PrivateRoot, "generations", plan.GenerationId);
        if (_active is { } previous && _core is { HasExited: false } && Inspect().TunTakenOver
            && previous.HomeServiceId == plan.HomeServiceId && previous.NonHomeServiceId == plan.NonHomeServiceId)
        {
            string previousDirectory = Path.Combine(MacHelperPaths.PrivateRoot, "generations", previous.GenerationId);
            string before = System.Text.Encoding.UTF8.GetString(HostFiles.ReadOwned(native, Path.Combine(previousDirectory, "xray.json"), 0, 1024 * 1024));
            string after = System.Text.Encoding.UTF8.GetString(HostFiles.ReadOwned(native, Path.Combine(directory, "xray.json"), 0, 1024 * 1024));
            if (RuntimeRoutingUpdate.CanReplace(before, after))
            {
                _diagnostics.Record("routing.replace", "validated routing update; preserve core, TUN and DNS");
                await RuntimeRoutingUpdate.ReplaceAsync(previousDirectory, directory, ReplaceRoutesAsync, cancellationToken).ConfigureAwait(false);
                _active = plan; _failure = null;
                _observer?.SetGeneration(plan.GenerationId); // Existing records retain their own generation and outbound labels.
                return;
            }
        }
        _diagnostics.Record("core.reconfigure", "validated listener, interface, DNS or outbound change requires core start");
        await RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        byte[] configBytes = HostFiles.ReadOwned(native, Path.Combine(directory, "xray.json"), 0, 1024 * 1024);
        using var config = JsonDocument.Parse(configBytes);
        var tun = config.RootElement.GetProperty("inbounds")[0].GetProperty("settings");
        _tunName = tun.GetProperty("name").GetString()!; _tunCidr = tun.GetProperty("gateway")[0].GetString()!;
        if (native.IsTunInterfacePresent(_tunName)) throw new RuntimeFailureException("tun.in-use");
        foreach (var inbound in config.RootElement.GetProperty("inbounds").EnumerateArray())
        {
            if (inbound.GetProperty("tag").GetString() != "resources") continue;
            using var portCheck = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try { portCheck.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, inbound.GetProperty("port").GetInt32())); }
            catch (System.Net.Sockets.SocketException error) when (error.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
            { throw new RuntimeFailureException("proxy.port-in-use"); }
            catch (System.Net.Sockets.SocketException error) when (error.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
            { throw new RuntimeFailureException("core.permission"); }
        }
        var snapshots = native.ReadDnsSnapshots().Where(snapshot => snapshot.ServiceId == plan.HomeServiceId || snapshot.ServiceId == plan.NonHomeServiceId).ToArray();
        if (snapshots.Length != 2) throw new RuntimeFailureException("dns.snapshot");
        var dns = new MacDnsRecoverySnapshot(1, Guid.NewGuid(), DateTimeOffset.UtcNow, snapshots); dns.Validate();
        HostFiles.AtomicWrite(native, JournalPath, JsonSerializer.SerializeToUtf8Bytes(new RecoveryJournal(dns), HelperHostJson.Default.RecoveryJournal));
        _dnsRestored = false; _failure = null;
        try
        {
            _observer = new XrayConnectionObserver(plan.GenerationId);
            _startup.Reset();
            var runtime = JsonNode.Parse(configBytes)!.AsObject();
            runtime["api"] = new JsonObject { ["tag"] = "egress-routing", ["listen"] = ControlSocket,
                ["services"] = new JsonArray("RoutingService") };
            HostFiles.AtomicWrite(native, Path.Combine(directory, "core.runtime.json"), System.Text.Encoding.UTF8.GetBytes(runtime.ToJsonString()));
            if (File.Exists(ControlSocket)) File.Delete(ControlSocket);
            _core = StartCore(directory, testOnly: false, _observer);
            var identity = native.ReadProcess(_core.Id);
            HostFiles.AtomicWrite(native, JournalPath, JsonSerializer.SerializeToUtf8Bytes(new RecoveryJournal(dns, _core.Id, identity.StartedAtMicroseconds), HelperHostJson.Default.RecoveryJournal));
            _active = plan;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (!native.IsTunTakeoverActive(_tunName, _tunCidr))
            {
                if (_core.HasExited) { _diagnostics.Record("core.start-exit", "exit=" + _core.ExitCode + "; code=" + _startup.Code); throw new RuntimeFailureException(_startup.Code); }
                try { await Task.Delay(100, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new RuntimeFailureException("tun.timeout"); }
            }
            // DNS only changes once the actual interface/address/IPv4+IPv6 route takeover exists.
            native.SetDns(snapshots, _tunCidr.Split('/')[0]);
        }
        catch (Exception error)
        {
            _diagnostics.Failure("core.start", error);
            _failure = "core-or-tun-failure";
            await RestoreAsync(CancellationToken.None).ConfigureAwait(false); throw;
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        RecoveryJournal? journal = null;
        if (File.Exists(JournalPath)) journal = JsonSerializer.Deserialize(HostFiles.ReadOwned(native, JournalPath, 0, 4 * 1024 * 1024), HelperHostJson.Default.RecoveryJournal)
            ?? throw new InvalidDataException("DNS recovery journal is invalid.");
        var outcome = await RecoverySteps.ExecuteAsync(async () =>
        {
            if (_core is not null)
            {
                await StopOwnedCoreAsync(_core).ConfigureAwait(false); _core.Dispose(); _core = null;
            }
            else if (journal is { CorePid: > 0 })
            {
                Process orphan;
                try { orphan = Process.GetProcessById(journal.CorePid); }
                catch (ArgumentException) { return; }
                using (orphan)
                {
                    if (orphan.HasExited) return;
                    var identity = native.ReadProcess(journal.CorePid);
                    if (identity.UserId != 0 || identity.StartedAtMicroseconds != journal.CoreStartedAtMicroseconds || identity.ExecutablePath != MacHelperPaths.InstalledXray)
                        return; // A reused PID is not an owned core and must never be signaled.
                    await StopOwnedCoreAsync(orphan).ConfigureAwait(false);
                }
            }
        }, () =>
        {
            if (journal is not null) { journal.Dns.Validate(); native.RestoreDns(journal.Dns.Services); }
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        _dnsRestored = outcome.DnsRestored;
        if (outcome.CoreStopped) _observer?.MarkUnavailable();
        if (outcome.CoreStopped) _active = null;
        if (!outcome.CoreStopped || !outcome.DnsRestored)
        { _failure = outcome.DnsRestored ? "core-stop-unconfirmed" : "dns-restore-failed"; throw new IOException("Network restoration remains incomplete."); }
        if (journal is not null)
        {
            File.Delete(JournalPath); HostFiles.SyncDirectory(MacHelperPaths.PrivateRoot);
        }
    }

    public MacHelperStatus Inspect()
    {
        bool running = _core is not null && !_core.HasExited;
        bool takenOver = running && _tunName is not null && _tunCidr is not null && native.IsTunTakeoverActive(_tunName, _tunCidr);
        if (_active is not null && (!running || !takenOver)) _failure ??= running ? "tun-unavailable" : "core-exited";
        return new(true, running, takenOver, _dnsRestored, running ? _core!.Id : 0, _active?.GenerationId, _failure, DateTimeOffset.UtcNow, installation.XraySha256);
    }
    public CoreConnectionSnapshot ReadConnections() => _observer?.Snapshot(DateTimeOffset.UtcNow)
        ?? CoreConnectionSnapshots.Unavailable(DateTimeOffset.UtcNow, "核心尚未运行，暂无连接观测。");
    private Process StartCore(string workingDirectory, bool testOnly, XrayConnectionObserver? observer = null)
    {
        var start = new ProcessStartInfo(MacHelperPaths.InstalledXray)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("run"); if (testOnly) start.ArgumentList.Add("-test");
        start.ArgumentList.Add("-config"); start.ArgumentList.Add(Path.Combine(workingDirectory, testOnly ? "xray.json" : "core.runtime.json"));
        XrayProcessEnvironment.Configure(start, workingDirectory);
        var child = Process.Start(start) ?? throw new IOException("Xray did not start.");
        _ = DrainCoreAsync(child.StandardOutput, observer);
        _ = DrainCoreAsync(child.StandardError, observer);
        return child;
    }
    private Task DrainCoreAsync(StreamReader reader, XrayConnectionObserver? observer)
        => XrayConnectionOutputPump.DrainAsync(reader, observer, _startup.Observe);
    private static async Task ReplaceRoutesAsync(string directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(MacHelperPaths.InstalledXray) { WorkingDirectory = directory,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "api", "adrules", "--server=unix://" + ControlSocket, "--timeout=8", "--append=false", Path.Combine(directory, "xray.json") }) start.ArgumentList.Add(argument);
        XrayProcessEnvironment.Configure(start, directory);
        using var child = Process.Start(start) ?? throw new RuntimeFailureException("routing.update");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var stdout = XrayConnectionOutputPump.DrainAsync(child.StandardOutput, null);
        var stderr = XrayConnectionOutputPump.DrainAsync(child.StandardError, null);
        try { await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (child.ExitCode != 0) throw new RuntimeFailureException("routing.update");
    }
    private static async Task StopOwnedCoreAsync(Process process)
    {
        if (process.HasExited) return;
        HostFiles.SignalTerminate(process.Id);
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); return; }
        catch (OperationCanceledException) { }
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
