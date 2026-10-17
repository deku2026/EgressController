namespace EgressController.SingBox.Runtime;

/// <summary>Prepare against desired settings while the active profile and TUN stay authoritative.</summary>
public static class PreparedRuntimeUpdate
{
    public static async Task<SingBoxApplyResult> ApplyAsync(SingBoxService service,
        Func<CancellationToken, Task<SingBoxRuntimeCandidate>> prepare, Action commit, Action rollback,
        CancellationToken token = default)
    {
        bool committed = false;
        SingBoxApplyResult result = await service.ApplyAsync(async cancellation =>
        {
            SingBoxRuntimeCandidate candidate = await prepare(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            committed = true; // rollback also repairs a partially failed persistence operation
            commit();
            return candidate;
        }, token).ConfigureAwait(false);
        if (!result.Succeeded && committed)
        {
            try { rollback(); }
            catch (Exception error) { return result with { ErrorMessage = result.ErrorMessage + "；配置回滚保存失败：" + error.Message }; }
        }
        return result;
    }
}
