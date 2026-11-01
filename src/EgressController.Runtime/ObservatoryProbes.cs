using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EgressController.Policy;

namespace EgressController.Runtime;

public sealed record ObservatoryResult(bool Available, double LatencyMilliseconds);
public interface IObservatoryProcess
{
    Task<ObservatoryResult> RunAsync(string executable, string json, string outboundTag, CancellationToken cancellationToken);
}

/// <summary>Real outbound health, using the pinned Xray observatory without an inbound, TUN or API listener.</summary>
public sealed class ObservatoryProbes(IObservatoryProcess process)
{
    public async Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, XrayCompileOptions options,
        string executable, CancellationToken cancellationToken = default, bool requiredOnly = false, IReadOnlyList<RequiredGroupProbe>? requested = null, IProgress<GroupProbe>? progress = null)
    {
        var results = new List<GroupProbe>();
        var observations = options.Probes.ToList();
        var working = new XrayCompileOptions { Interfaces = options.Interfaces.ToList(), Probes = observations,
            Now = options.Now, ProbeMaxAge = options.ProbeMaxAge, PreparedRuleSources = options.PreparedRuleSources.ToList(),
            ChinaGeoSiteFile = options.ChinaGeoSiteFile, ChinaGeoIpFile = options.ChinaGeoIpFile, ChinaTag = options.ChinaTag };
        using var throttle = new SemaphoreSlim(6, 6);
        var urls = document.Groups.Select(group => group.TestUrl).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) urls = ["https://www.gstatic.com/generate_204"];
        var attempted = new HashSet<(string NodeId, string Url)>();
        do
        {
            var requests = requested ?? (requiredOnly ? RequiredGroupProbes.Missing(document, working)
                : document.Nodes.Where(node => node.IsSupported).SelectMany(node => urls.Select(url => new RequiredGroupProbe(node.Id, url))).ToArray());
            requests = requests.Where(request => attempted.Add((request.NodeId, request.TestUrl))).ToArray();
            if (requests.Count == 0) break;
            var tasks = requests.Select(async item =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string outbound = item.NodeId == GroupGraph.Direct ? "nonhome" : item.NodeId == GroupGraph.Reject ? "blocked" : item.NodeId;
                    var draft = ProbeDocument(document, item.NodeId);
                    var compiled = XrayConfigCompiler.Compile(draft, working);
                    ObservatoryResult measured = new(false, 0);
                    if (compiled.IsSuccess)
                    {
                        var root = JsonNode.Parse(compiled.Json!)!.AsObject();
                        root["inbounds"] = new JsonArray();
                        root["routing"]!["rules"] = new JsonArray(
                            new JsonObject { ["inboundTag"] = new JsonArray("dns-query"), ["outboundTag"] = "nonhome" },
                            new JsonObject { ["network"] = "tcp,udp", ["outboundTag"] = outbound });
                        root["log"] = new JsonObject { ["loglevel"] = "info" };
                        root["observatory"] = new JsonObject
                        {
                            ["subjectSelector"] = new JsonArray(outbound),
                            ["probeURL"] = item.TestUrl,
                            ["probeInterval"] = "1h",
                            ["enableConcurrency"] = false
                        };
                        measured = await process.RunAsync(executable, root.ToJsonString(), outbound, cancellationToken).ConfigureAwait(false);
                    }
                    var observation = new GroupProbe(item.NodeId, measured.Available, measured.LatencyMilliseconds, DateTimeOffset.UtcNow, item.TestUrl);
                    lock (results) results.Add(observation);
                    progress?.Report(observation);
                }
                finally { throttle.Release(); }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
            observations.Clear(); observations.AddRange(options.Probes); observations.AddRange(results);
            if (results.Count > 0 && results.Max(result => result.ObservedAt) > working.Now) working.Now = results.Max(result => result.ObservedAt);
        } while (requiredOnly);
        // Required-only callers can atomically replace their probe snapshot without dropping fresh cached observations.
        return requiredOnly ? observations : results;
    }

    // Keep only this node and its dialer dependencies. Unrelated removed subscriptions and
    // thousands of application/rule rows neither block a test nor get compiled per node.
    public static MacPolicyDocument ProbeDocument(MacPolicyDocument source, string nodeId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!ids.Add(id)) return;
            if (source.Nodes.FirstOrDefault(node => node.Id == id)?.DialerProxyId is { } dialer) Visit(dialer);
            if (source.Groups.FirstOrDefault(group => group.Id == id) is { } group)
                foreach (string member in group.MemberIds) Visit(member);
        }
        Visit(nodeId);
        string selector = "probe-selector"; while (ids.Contains(selector)) selector += "-x";
        var draft = new MacPolicyDocument
        {
            Interfaces = source.Interfaces, Dns = source.Dns, Tun = source.Tun,
            Nodes = source.Nodes.Where(node => ids.Contains(node.Id)).ToList(),
            Groups = source.Groups.Where(group => ids.Contains(group.Id)).ToList()
        };
        draft.Groups.Add(new() { Id = selector, Name = "Probe", Kind = GroupKind.Select, MemberIds = [nodeId], SelectedMemberId = nodeId });
        draft.Defaults.Global = ExitChoice.Proxy(selector);
        return draft;
    }

    // Exact message emitted by Xray v26.10.29 app/observatory/observer.go. Unknown output never means success.
    public static ObservatoryResult? ParseLine(string line, string tag)
    {
        if (line.Length > 8192) return null;
        const string module = "app/observatory:";
        int moduleOffset = line.IndexOf(module, StringComparison.Ordinal);
        if (moduleOffset < 0) return null;
        string message = line[(moduleOffset + module.Length)..].TrimStart();
        string alive = "the outbound " + tag + " is alive:";
        if (message.StartsWith(alive, StringComparison.Ordinal) && double.TryParse(message.AsSpan(alive.Length).Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds is >= 0 and < 60)
            return new(true, seconds * 1000);
        return message.StartsWith("the outbound " + tag + " is dead: GET request failed:", StringComparison.Ordinal) ? new(false, 0) : null;
    }
}

/// <summary>Runs only an owned, temporary no-inbound probe process; a gate is checked before any filesystem/process effect.</summary>
public sealed class XrayObservatoryProcess(Action requireProduction, string privateRoot) : IObservatoryProcess
{
    public async Task<ObservatoryResult> RunAsync(string executable, string json, string outboundTag, CancellationToken cancellationToken)
    {
        requireProduction();
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["inbounds"] is not JsonArray { Count: 0 } || root["api"] is not null)
            throw new InvalidDataException("Probe configurations cannot contain listeners or TUN.");
        DurableFiles.CreatePrivateDirectory(privateRoot);
        string session = Path.Combine(privateRoot, Guid.NewGuid().ToString("N"));
        DurableFiles.CreatePrivateDirectory(session);
        Process? child = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            string config = Path.Combine(session, "probe.json");
            DurableFiles.Write(config, Encoding.UTF8.GetBytes(json));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = session
            };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-config"); start.ArgumentList.Add(config);
            XrayProcessEnvironment.Configure(start, session);
            start.Environment["NO_COLOR"] = "1";
            child = Process.Start(start) ?? throw new IOException("The outbound probe process did not start.");
            var answer = new TaskCompletionSource<ObservatoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task stdout = ReadAsync(child.StandardOutput), stderr = ReadAsync(child.StandardError);
            Task exited = child.WaitForExitAsync(timeout.Token);
            Task first = await Task.WhenAny(answer.Task, exited).WaitAsync(timeout.Token).ConfigureAwait(false);
            return first == answer.Task ? await answer.Task.ConfigureAwait(false) : new(false, 0);

            async Task ReadAsync(StreamReader reader)
            {
                try
                {
                    while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                        if (ObservatoryProbes.ParseLine(line, outboundTag) is { } result) answer.TrySetResult(result);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, 0); }
        finally
        {
            timeout.Cancel();
            if (child is not null)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                child.Dispose();
            }
            if (Directory.Exists(session)) Directory.Delete(session, recursive: true);
        }
    }
}

public static class XrayProcessEnvironment
{
    public static void Configure(ProcessStartInfo start, string assetDirectory)
    {
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("XRAY_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("xray.", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["XRAY_LOCATION_ASSET"] = assetDirectory;
    }
}
