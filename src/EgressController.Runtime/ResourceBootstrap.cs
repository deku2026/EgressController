using System.Text.Json.Nodes;
using EgressController.Policy;

namespace EgressController.Runtime;

public sealed partial class ConfigurationCoordinator
{
    private RuntimeGeneration? _beforeBootstrap;
    private bool _bootstrapActive;

    public Task BeginResourceBootstrapAsync(MacPolicyDocument document, XrayCompileOptions options,
        CancellationToken cancellationToken = default) => ApplyResourceBootstrapAsync(document, options, false, cancellationToken);

    public Task CompleteResourceBootstrapAsync(MacPolicyDocument document, XrayCompileOptions options,
        CancellationToken cancellationToken = default) => ApplyResourceBootstrapAsync(document, options, true, cancellationToken);

    private async Task ApplyResourceBootstrapAsync(MacPolicyDocument document, XrayCompileOptions options,
        bool completing, CancellationToken cancellationToken)
    {
        var bootstrapOptions = new XrayCompileOptions { Interfaces = options.Interfaces.ToList(), Probes = options.Probes.ToList(),
            Now = options.Now, ProbeMaxAge = options.ProbeMaxAge, ResourceBootstrap = true, DeferAutomaticGroupSelection = !completing };
        var compiled = XrayConfigCompiler.Compile(document, bootstrapOptions);
        if (!compiled.IsSuccess) throw new InvalidDataException(string.Join(" ", compiled.Diagnostics.Select(item => item.Message)));
        string policy = PolicyYaml.Write(document);
        var assets = new Dictionary<string, byte[]>();
        var generation = new RuntimeGeneration(GenerationIdentity(policy, compiled.Json!, assets), policy, compiled.Json!, assets);
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bootstrapActive && !completing) throw new InvalidOperationException("A resource preparation already owns the runtime.");
            // A ready, unchanged resource route serves refreshes without touching the running TUN.
            if (Applied is { } active && ResourceRouteIdentity(active.XrayJson) == ResourceRouteIdentity(generation.XrayJson)) return;
            if (completing && !_bootstrapActive) throw new InvalidOperationException("Resource preparation no longer owns the runtime.");
            if (!_bootstrapActive) _beforeBootstrap = Applied;
            await core.ValidateAsync(generation, cancellationToken).ConfigureAwait(false);
            Event("configuration.bootstrap", completing ? "TUN 已建立：应用所选分组的资源出口" : "用户连接：先建立 TUN，出口在线状态不作为启动条件", generation.Id);
            _bootstrapActive = true;
            try { await core.ApplyAsync(generation, cancellationToken).ConfigureAwait(false); Applied = generation; }
            catch
            {
                try { await core.RestoreAsync(_beforeBootstrap, CancellationToken.None).ConfigureAwait(false); Applied = _beforeBootstrap; }
                catch { Applied = null; _bootstrapActive = false; _beforeBootstrap = null; Event("protection.unavailable", "资源连接失败且恢复未确认", null); throw new RuntimeFailureException("recovery.failed"); }
                _bootstrapActive = false; _beforeBootstrap = null; throw;
            }
        }
        finally { _applyLock.Release(); }
    }

    public async Task EndResourceBootstrapAsync(bool applied)
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_bootstrapActive) return;
            if (!applied)
            {
                Event("configuration.restoring", "资源准备取消或失败，恢复连接前的网络状态", _beforeBootstrap?.Id);
                try { await core.RestoreAsync(_beforeBootstrap, CancellationToken.None).ConfigureAwait(false); Applied = _beforeBootstrap; }
                catch { Applied = null; Event("protection.unavailable", "资源准备后恢复未确认", null); throw new RuntimeFailureException("recovery.failed"); }
            }
        }
        finally { _bootstrapActive = false; _beforeBootstrap = null; _applyLock.Release(); }
    }

    private static string ResourceRouteIdentity(string json)
    {
        var root = JsonNode.Parse(json)!;
        var rules = root["routing"]!["rules"]!.AsArray();
        string? target = rules.FirstOrDefault(rule => (string?)rule?["ruleTag"] == "resources")?["outboundTag"]?.GetValue<string>();
        if (target is null) return "";
        var outbounds = root["outbounds"]!.AsArray();
        var selected = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        while (target is not null && seen.Add(target))
        {
            var outbound = outbounds.Single(item => (string?)item?["tag"] == target)!;
            selected.Add(outbound.ToJsonString()); target = (string?)outbound["streamSettings"]?["sockopt"]?["dialerProxy"];
        }
        selected.Add(root["inbounds"]!.ToJsonString()); selected.Add(root["dns"]!.ToJsonString());
        selected.Add(outbounds.Single(item => (string?)item?["tag"] == "nonhome")!.ToJsonString());
        return string.Join("\n", selected);
    }
}
