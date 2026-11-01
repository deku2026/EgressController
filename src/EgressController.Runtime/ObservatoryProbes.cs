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
        string executable, CancellationToken cancellationToken = default, bool requiredOnly = false, IReadOnlyList<RequiredGroupProbe>? requested = null)
    {
        var results = new List<GroupProbe>();
        var observations = options.Probes.ToList();
        var working = new XrayCompileOptions { Interfaces = options.Interfaces.ToList(), Probes = observations,
            Now = options.Now, ProbeMaxAge = options.ProbeMaxAge, PreparedRuleSources = options.PreparedRuleSources.ToList(),
            ChinaGeoSiteFile = options.ChinaGeoSiteFile, ChinaGeoIpFile = options.ChinaGeoIpFile, ChinaTag = options.ChinaTag };
        using var throttle = new SemaphoreSlim(3, 3);
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
                    var draft = PolicyYaml.Read(PolicyYaml.Write(document)).Document!;
                    draft.Applications.Clear(); draft.RuleSets.Clear();
                    // Internal health requests still pass the public group-only route contract.
                    string probeGroupId = "probe-selector";
                    while (draft.Nodes.Any(node => node.Id == probeGroupId) || draft.Groups.Any(group => group.Id == probeGroupId) || draft.Providers.Any(provider => provider.Id == probeGroupId)) probeGroupId += "-x";
                    draft.Groups.Add(new() { Id = probeGroupId, Name = "Internal probe selector", Kind = GroupKind.Select,
                        MemberIds = [item.NodeId], SelectedMemberId = item.NodeId });
                    draft.Defaults.Global = ExitChoice.Proxy(probeGroupId);
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
                    lock (results) results.Add(new(item.NodeId, measured.Available, measured.LatencyMilliseconds, DateTimeOffset.UtcNow, item.TestUrl));
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
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
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
