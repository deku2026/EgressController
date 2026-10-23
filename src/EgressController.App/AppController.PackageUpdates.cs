using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Launcher.Discovery;
using EgressController.Windows.Process;

namespace EgressController.App;

public sealed partial class AppController
{
    private Task? _packageUpdatesTask;
    private string _packageUpdateNotice = "";
    private string _routingChangeReason = "已保存的应用路径发生变化";

    private async Task PackageUpdatesLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                await _inventoryGate.WaitAsync(token).ConfigureAwait(false);
                try { await SynchronizePackagePathsAsync(token).ConfigureAwait(false); }
                finally { _inventoryGate.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    // Caller owns the inventory gate. Only registered package metadata and known relative
    // file paths are checked. Opening an app, observing a PID or telemetry never calls this.
    private async Task SynchronizePackagePathsAsync(CancellationToken token)
    {
        try
        {
            var upgrade = WindowsPackageUpgradeSource.Read(Profile);
            if (!upgrade.HasChanges) { _packageUpdateNotice = ""; return; }
            await _configurationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Re-evaluate against current selections so a concurrent route edit/deselection wins.
                var next = upgrade.Apply(_profile);
                if (ReferenceEquals(next, _profile)) return;
                next = ApplicationPathAliases.Expand(next.NormalizeAndValidate(), new ExecutablePathCanonicalizer());
                ValidateProtectionConflicts(next, ResolveProxyBindings(next, token).OwnerPaths, updateExclusions: false);
                string cause = "Windows 应用升级，跟随同一包身份更新已保存的 EXE 路径：\n"
                    + string.Join("\n", upgrade.Applications.SelectMany(app => app.Paths.Select(path => $"{app.Name}: {path.Previous} → {path.Current}")));

                // This is newly discovered identity for an ALREADY selected app. Retain desired
                // membership on a later core-apply failure, so both guardians protect the new EXEs
                // while the existing runtime monitor retries. Never roll back to old-only paths.
                _profileStore.Save(next);
                _routingChangeReason = cause;
                _routingDirty = true;
                _profileApplied = false;
                _profile = next;
                RefreshProtectedInventory();
                Volatile.Write(ref _protection, new(false, "应用升级路径尚未应用，正在自动恢复"));
                _protectionStatus = "保护中：应用升级路径尚未应用，正在自动恢复";
                Diagnostics.Info("TUN 配置变更", cause);
                var targets = _targets.All().Select(target => upgrade.UpdateTarget(target, next)).ToArray();
                _targets.ReplaceAll(targets);
                Visuals.Update(targets, next);
                try { _applicationCatalog.Save(targets); }
                catch (Exception error) { _catalogNotice = "应用缓存保存失败：" + error.Message; }
                if (_guardAgent is not null) await _guardAgent.SyncAsync(token).ConfigureAwait(false);
                _packageUpdateNotice = "";
                SetMessage("应用升级路径已保存，正在应用原有分流。");
                // Startup's supervisor or the running core's existing monitor applies this exact
                // desired profile. No second start/retry loop and no extra health/network test.
            }
            finally { _configurationGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _packageUpdateNotice = "应用升级路径检查未完成，将自动重试：" + error.Message;
            Diagnostics.Report("TUN 应用路径同步", "application.package-update", _packageUpdateNotice, error);
        }
    }
}
