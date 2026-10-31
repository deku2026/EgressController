using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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
            || manifest.Files.Keys.Any(name => !ConfigurationCoordinator.SafeArtifactName(name) || name is "runtime-manifest.json" or "recovery.json"))
            throw new InvalidDataException("Invalid runtime manifest.");
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal); long total = 0;
        foreach (var (name, hash) in manifest.Files)
        {
            byte[] bytes = HostFiles.ReadOwned(native, Path.Combine(plan.GenerationDirectory, name), installation.AuthorizedUserId, 64 * 1024 * 1024);
            if ((total += bytes.Length) > 256 * 1024 * 1024 || !MacHelperRequestValidator.ContentMatches(bytes, hash)) throw new InvalidDataException("Runtime contents changed or exceeded limits.");
            content.Add(name, bytes);
        }
        var interfaces = new NativeMacPlatform(LiveOperationGate.ForExplicitLiveUse()).ReadInterfaces();
        string home = interfaces.Single(item => item.ServiceId == plan.HomeServiceId).BsdName;
        string nonHome = interfaces.Single(item => item.ServiceId == plan.NonHomeServiceId).BsdName;
        var validation = MacHelperConfigurationValidator.Validate(content["xray.json"], home, nonHome);
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
        finally { if (!test.HasExited) { test.Kill(true); await test.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
        if (test.ExitCode != 0) throw new InvalidDataException("Xray configuration validation failed.");
    }

    public async Task ApplyAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken)
    {
        await RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        string directory = Path.Combine(MacHelperPaths.PrivateRoot, "generations", plan.GenerationId);
        byte[] configBytes = HostFiles.ReadOwned(native, Path.Combine(directory, "xray.json"), 0, 1024 * 1024);
        using var config = JsonDocument.Parse(configBytes);
        var tun = config.RootElement.GetProperty("inbounds")[0].GetProperty("settings");
        _tunName = tun.GetProperty("name").GetString()!; _tunCidr = tun.GetProperty("gateway")[0].GetString()!;
        var snapshots = native.ReadDnsSnapshots().Where(snapshot => snapshot.ServiceId == plan.HomeServiceId || snapshot.ServiceId == plan.NonHomeServiceId).ToArray();
        if (snapshots.Length != 2) throw new InvalidDataException("Selected DNS services are not available.");
        var dns = new MacDnsRecoverySnapshot(1, Guid.NewGuid(), DateTimeOffset.UtcNow, snapshots); dns.Validate();
        HostFiles.AtomicWrite(native, JournalPath, JsonSerializer.SerializeToUtf8Bytes(new RecoveryJournal(dns), HelperHostJson.Default.RecoveryJournal));
        _dnsRestored = false; _failure = null;
        try
        {
            _observer = new XrayConnectionObserver(plan.GenerationId);
            _core = StartCore(directory, testOnly: false, _observer);
            var identity = native.ReadProcess(_core.Id);
            HostFiles.AtomicWrite(native, JournalPath, JsonSerializer.SerializeToUtf8Bytes(new RecoveryJournal(dns, _core.Id, identity.StartedAtMicroseconds), HelperHostJson.Default.RecoveryJournal));
            _active = plan;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (!native.IsTunTakeoverActive(_tunName, _tunCidr))
            {
                if (_core.HasExited) throw new IOException("Xray exited before TUN takeover.");
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            // DNS only changes once the actual interface/address/IPv4+IPv6 route takeover exists.
            native.SetDns(snapshots, _tunCidr.Split('/')[0]);
        }
        catch
        {
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
    private static Process StartCore(string workingDirectory, bool testOnly, XrayConnectionObserver? observer = null)
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
        start.ArgumentList.Add("-config"); start.ArgumentList.Add(Path.Combine(workingDirectory, "xray.json"));
        XrayProcessEnvironment.Configure(start, workingDirectory);
        var child = Process.Start(start) ?? throw new IOException("Xray did not start.");
        _ = XrayConnectionOutputPump.DrainAsync(child.StandardOutput, observer);
        _ = XrayConnectionOutputPump.DrainAsync(child.StandardError, observer);
        return child;
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
