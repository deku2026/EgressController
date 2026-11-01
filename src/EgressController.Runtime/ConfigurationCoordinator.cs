using System.Security.Cryptography;
using System.Text;
using EgressController.Policy;

namespace EgressController.Runtime;

public enum ApplyDisposition { Applied, Unchanged, Rejected, Failed, Canceled }
public sealed record ApplyResult(ApplyDisposition Disposition, string? GenerationId, IReadOnlyList<PolicyDiagnostic> Diagnostics)
{
    public bool Succeeded => Disposition is ApplyDisposition.Applied or ApplyDisposition.Unchanged;
}

public sealed record RuntimeGeneration(string Id, string PolicyYaml, string XrayJson, IReadOnlyDictionary<string, byte[]> Assets)
{
    public string RuntimeFingerprint => ConfigurationCoordinator.Fingerprint(XrayJson, Assets);
}
public sealed record LifecycleEvent(DateTimeOffset Time, string Kind, string Cause, string? GenerationId = null);
public interface ILifecycleLog { void Append(LifecycleEvent entry); }
public interface IRuntimeGenerationStore
{
    Task<RuntimeGeneration?> ReadCurrentAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(RuntimeGeneration generation, CancellationToken cancellationToken = default);
    Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken = default);
}
public interface ICoreRuntime
{
    Task ValidateAsync(RuntimeGeneration generation, CancellationToken cancellationToken);
    Task ApplyAsync(RuntimeGeneration generation, CancellationToken cancellationToken);
    Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken);
}

/// <summary>Configuration changes are the only input that can apply a new runtime generation.</summary>
public sealed partial class ConfigurationCoordinator(ICoreRuntime core, IRuntimeGenerationStore store, ILifecycleLog log)
{
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    public RuntimeGeneration? Applied { get; private set; }
    // Keep desired executable inventories even when applying the new configuration fails.
    public string? DesiredPolicyYaml { get; private set; }

    public async Task ForgetConfigurationAsync(CancellationToken token = default)
    {
        await _applyLock.WaitAsync(token).ConfigureAwait(false);
        try { await store.RestoreAsync(null, token).ConfigureAwait(false); }
        finally { Applied = null; DesiredPolicyYaml = null; _bootstrapActive = false; _beforeBootstrap = null; _applyLock.Release(); }
    }

    public async Task<bool> StopAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _bootstrapActive = false; _beforeBootstrap = null; // A later preparation cleanup cannot undo an explicit stop.
            Event("runtime.stopping", reason, Applied?.Id);
            // A user stop restores network state without deleting the saved configuration.
            await core.RestoreAsync(null, CancellationToken.None).ConfigureAwait(false);
            Applied = null;
            Event("runtime.stopped", reason, null);
            return true;
        }
        catch (Exception)
        {
            Applied = null;
            Event("protection.unavailable", reason, null);
            return false;
        }
        finally { _applyLock.Release(); }
    }

    public async Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyDictionary<string, byte[]> assets, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string policy = PolicyYaml.Write(document);
        DesiredPolicyYaml = policy;
        var compiled = XrayConfigCompiler.Compile(document, options);
        if (!compiled.IsSuccess)
            return new(ApplyDisposition.Rejected, Applied?.Id, compiled.Diagnostics);
        // Take a snapshot before any await: callers may continue editing their draft.
        var snapshot = assets.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        if (snapshot.Keys.Any(name => !SafeArtifactName(name)))
            return new(ApplyDisposition.Rejected, Applied?.Id, [new("artifact.path", "生成资源名称必须是单个安全文件名。")]);
        var resourceDiagnostics = RuntimeAssetValidator.Validate(compiled.Json!, snapshot);
        if (resourceDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return new(ApplyDisposition.Rejected, Applied?.Id, resourceDiagnostics);
        var desired = new RuntimeGeneration(GenerationIdentity(policy, compiled.Json!, snapshot), policy, compiled.Json!, snapshot);
        bool locked = false;
        RuntimeGeneration? previous = null;
        RuntimeGeneration? previousRuntime = null;
        bool runtimeAttempted = false;
        bool publicationAttempted = false;
        try
        {
            await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            locked = true;
            previousRuntime = Applied;
            previous = await store.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (previousRuntime?.Id == desired.Id && previous?.Id == desired.Id)
            {
                Applied = previous;
                return new(ApplyDisposition.Unchanged, desired.Id, compiled.Diagnostics);
            }
            if (previousRuntime?.RuntimeFingerprint == desired.RuntimeFingerprint)
            {
                publicationAttempted = true;
                await store.PublishAsync(desired, cancellationToken).ConfigureAwait(false);
                Applied = desired;
                Event("configuration.saved", reason, desired.Id);
                return new(ApplyDisposition.Unchanged, desired.Id, compiled.Diagnostics);
            }
            // Validation must finish before the old core/settings are changed.
            await core.ValidateAsync(desired, cancellationToken).ConfigureAwait(false);
            Event("configuration.apply", reason, desired.Id);
            runtimeAttempted = true;
            await core.ApplyAsync(desired, cancellationToken).ConfigureAwait(false);
            publicationAttempted = true;
            await store.PublishAsync(desired, cancellationToken).ConfigureAwait(false);
            Applied = desired;
            Event("configuration.applied", reason, desired.Id);
            return new(ApplyDisposition.Applied, desired.Id, compiled.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool restored = !(runtimeAttempted || publicationAttempted)
                || await RollbackAsync(previousRuntime, previous, reason, runtimeAttempted).ConfigureAwait(false);
            if (!restored)
            {
                Event("protection.unavailable", reason, null);
                return new(ApplyDisposition.Failed, null, [new("apply.rollback-failed", "取消后恢复失败，保护不可用。请查看诊断。")]);
            }
            return new(ApplyDisposition.Canceled, previousRuntime?.Id, []);
        }
        catch (Exception error)
        {
            bool restored = !(runtimeAttempted || publicationAttempted)
                || await RollbackAsync(previousRuntime, previous, reason, runtimeAttempted).ConfigureAwait(false);
            Event(restored ? "configuration.failed" : "protection.unavailable", reason + (error is RuntimeFailureException diagnosis ? " · " + diagnosis.Code : ""), previous?.Id);
            return new(ApplyDisposition.Failed, Applied?.Id,
                [new(restored && error is RuntimeFailureException known ? known.Code : restored ? "apply.failed" : "apply.rollback-failed", restored && error is RuntimeFailureException failure ? failure.Message : restored
                    ? "配置未应用，已保留上一份可用配置。请查看诊断后重试。"
                    : "配置恢复失败，保护不可用。请停止连接并查看诊断。")]);
        }
        finally { if (locked) _applyLock.Release(); }
    }

    private async Task<bool> RollbackAsync(RuntimeGeneration? previousRuntime, RuntimeGeneration? previousStored, string cause, bool restoreCore)
    {
        bool restored = true;
        // Recover the running core and persisted pointer independently. A failed core
        // restoration must not leave a partially published generation as the next startup.
        try
        {
            // Caller cancellation must never cancel recovery of the previous network state.
            if (restoreCore) await core.RestoreAsync(previousRuntime, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { restored = false; }
        try { await store.RestoreAsync(previousStored, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { restored = false; }
        Applied = restored ? previousRuntime : null;
        if (restored)
        {
            Event("configuration.restored", cause, previousRuntime?.Id);
        }
        return restored;
    }

    private void Event(string kind, string cause, string? generation)
    {
        try { log.Append(new(DateTimeOffset.UtcNow, kind, cause, generation)); }
        catch (Exception) { /* Diagnostics must never change a protection decision. */ }
    }

    public static bool SafeArtifactName(string name) => !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".." && name.IndexOfAny(['/', '\\', '\0']) < 0
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string Fingerprint(string json, IReadOnlyDictionary<string, byte[]> assets)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(json);
        foreach (var (name, content) in assets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(name);
            hash.AppendData(SHA256.HashData(content));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
    }

    public static string GenerationIdentity(string yaml, string json, IReadOnlyDictionary<string, byte[]> assets)
        => Fingerprint(yaml + "\0" + json, assets);
}

public sealed class InMemoryGenerationStore : IRuntimeGenerationStore
{
    private RuntimeGeneration? _current;
    public bool FailPublication { get; set; }
    public Task<RuntimeGeneration?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_current); }
    public Task PublishAsync(RuntimeGeneration generation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailPublication) throw new IOException("Injected publication failure.");
        _current = generation; return Task.CompletedTask;
    }
    public Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); _current = generation; return Task.CompletedTask; }
}

public sealed class MockCoreRuntime : ICoreRuntime
{
    public int ApplyCount { get; private set; }
    public int RestartCount { get; private set; }
    public int RouteUpdateCount { get; private set; }
    public int RestoreCount { get; private set; }
    public RuntimeGeneration? Current { get; private set; }
    public bool FailValidation { get; set; }
    public bool FailApply { get; set; }
    public Task ValidateAsync(RuntimeGeneration generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailValidation) throw new InvalidOperationException("Injected configuration validation failure.");
        return Task.CompletedTask;
    }
    public Task ApplyAsync(RuntimeGeneration generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ApplyCount++;
        if (FailApply) throw new IOException("Injected core apply failure.");
        if (Current is not null && RuntimeRoutingUpdate.CanReplace(Current.XrayJson, generation.XrayJson)) RouteUpdateCount++; else RestartCount++;
        Current = generation; return Task.CompletedTask;
    }
    public Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken)
    { RestoreCount++; Current = generation; return Task.CompletedTask; }
}

public sealed class BoundedLifecycleLog(int capacity = 200) : ILifecycleLog
{
    private readonly Queue<LifecycleEvent> _events = new();
    public IReadOnlyList<LifecycleEvent> Events { get { lock (_events) return _events.ToArray(); } }
    public void Append(LifecycleEvent entry)
    {
        lock (_events)
        {
            _events.Enqueue(entry);
            while (_events.Count > Math.Max(1, capacity)) _events.Dequeue();
        }
    }
}
