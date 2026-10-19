using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Launcher.Discovery;
using EgressController.State.Applications;

namespace EgressController.App;

public sealed partial class AppController
{
    private readonly ApplicationCatalogStore _applicationCatalog;
    private readonly SemaphoreSlim _inventoryGate = new(1);
    private readonly object _categoryPublicationGate = new();
    private IReadOnlyDictionary<string, string> _applicationCategories = new Dictionary<string, string>();
    private string _catalogNotice = "";
    public IReadOnlyList<LaunchTarget> CachedTargets => _targets.All();
    public LaunchTarget? FindApplication(string key) => _targets.Find(key);
    public IReadOnlyDictionary<string, string> ApplicationCategories => Volatile.Read(ref _applicationCategories);

    private void LoadApplicationCatalog()
    {
        try
        {
            foreach (var target in _applicationCatalog.Read()) _targets.Add(target);
            ReloadCategories();
        }
        catch (Exception error) { _catalogNotice = "应用缓存读取失败：" + error.Message; }
        foreach (var target in ApplicationCatalogProjection.RestoreSavedTargets(_targets.All(), _profile.Applications)) _targets.Add(target);
        Visuals.Update(_targets.All(), _profile);
    }

    public async Task<ControllerOperationResult> SetApplicationCategoryAsync(LaunchTarget target, string? category)
    {
        try
        {
            await Task.Run(() =>
            {
                lock (_categoryPublicationGate)
                {
                    _applicationCatalog.SetCategory(target, category);
                    Volatile.Write(ref _applicationCategories, _applicationCatalog.Categories());
                }
            }).ConfigureAwait(false);
            return ControllerOperationResult.Success();
        }
        catch (Exception error) { return ControllerOperationResult.Failure("分类保存失败：" + error.Message); }
    }

    private void ReloadCategories()
    { lock (_categoryPublicationGate) Volatile.Write(ref _applicationCategories, _applicationCatalog.Categories()); }

    private async Task<LaunchTarget> BuildInventoryAsync(LaunchTarget target, CancellationToken token)
    {
        await _inventoryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A catalog refresh may have completed while this action waited for the gate.
            // Use the current roots (including newly associated package data) for this explicit scan.
            target = _targets.Find(target.DiscoveryKey) ?? target;
            var paths = await Task.Run(() => target.Kind == LaunchKind.CliNative
                ? target.OwnedExecutables : ExecutableInventory.CollectChecked(ExecutableInventory.AvailableRoots(target.OwnedRoots), target.CanonicalExecutable, token,
                    ApplicationInventoryBoundaries.ExcludedRoots(target, _targets.All())), token).ConfigureAwait(false);
            var updated = target.WithInventory(paths.Concat(target.OwnedExecutables).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            _applicationCatalog.Save([updated]);
            // Registry replacement is explicit: the cache's older count must not win quality scoring.
            var all = _targets.All().Where(item => item.DiscoveryKey != target.DiscoveryKey).Append(updated).ToArray();
            _targets.ReplaceAll(all);
            Visuals.Update(all, _profile);
            return updated;
        }
        finally { _inventoryGate.Release(); }
    }

    public async Task<ControllerOperationResult> RefreshApplicationInventoryAsync(LaunchTarget target)
    {
        try
        {
            var updated = await BuildInventoryAsync(target, _lifetimeCts.Token).ConfigureAwait(false);
            var saved = _profile.Applications.Where(item => updated.SelectionKeys.Contains(item.DiscoveryKey)).ToArray();
            if (saved.Select(item => item.Target).Distinct().Count() > 1)
                return ControllerOperationResult.Failure("旧辅助项使用不同出口，请先明确统一出口再更新清单。");
            return saved.Length == 0 ? ControllerOperationResult.Success()
                : await SetApplicationsRouteAsync([updated], true, saved[0].Target).ConfigureAwait(false);
        }
        catch (Exception error) { return ControllerOperationResult.Failure("EXE 清单未更新：" + error.Message); }
    }

    public async Task<ControllerOperationResult> AddManualApplicationAsync(string path, string? replaceKey = null)
    {
        await _inventoryGate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                string full = Path.GetFullPath(path);
                bool file = File.Exists(full) && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (file && InstalledApplicationResolver.IsInstaller(full)) throw new IOException("请选择应用主程序，不要选择安装器或卸载程序。");
                string root = file ? Path.GetDirectoryName(full)! : full;
                ApplicationDirectoryBoundary.RequireScoped(root);
                if (!Directory.Exists(root)) throw new IOException("安装目录不存在。");
                var previous = replaceKey is null ? null : _targets.All().FirstOrDefault(item => item.DiscoveryKey == replaceKey);
                var resolved = InstalledApplicationResolver.Resolve(new(replaceKey ?? "manual:" + full.ToLowerInvariant(), previous?.Name ?? (file ? Path.GetFileNameWithoutExtension(full) : Path.GetFileName(root)),
                    root, file ? full : null, file ? full : null, null, "手动指定"), File.Exists, Directory.Exists,
                    directory => Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly));
                var target = new LaunchTarget { Id = resolved.Id, SavedDiscoveryKey = resolved.DiscoveryKey, Name = resolved.Name,
                    Kind = resolved.Kind, Command = resolved.Command, CanonicalExecutable = resolved.CanonicalExecutable,
                    OwnedRoots = resolved.OwnedRoots, OwnedExecutables = [], InventoryReady = false, IsManual = true,
                    RelatedDiscoveryKeys = resolved.RelatedDiscoveryKeys, IconPath = resolved.IconPath, Source = resolved.Source };
                _applicationCatalog.Save([target]);
                var all = _targets.All().Where(item => !target.SelectionKeys.Contains(item.DiscoveryKey)).Append(target).ToArray();
                _targets.ReplaceAll(ApplicationCatalogProjection.Project(all));
                Visuals.Update(_targets.All(), _profile);
            }).ConfigureAwait(false);
            return ControllerOperationResult.Success();
        }
        catch (Exception error) { return ControllerOperationResult.Failure("添加应用失败：" + error.Message); }
        finally { _inventoryGate.Release(); }
    }
}
