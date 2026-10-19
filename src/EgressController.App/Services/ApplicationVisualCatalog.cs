using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Launcher.Discovery;

namespace EgressController.App.Services;

public sealed class ProcessVisual : ObservableObject
{
    private string _name;
    private Bitmap? _icon;
    private bool _isCli;
    internal string? IconKey { get; set; }
    internal ProcessVisual(string key, string name, bool cli) { Key = key; _name = name; IsCli = cli; }
    public string Key { get; internal set; }
    public string Name { get => _name; internal set => SetProperty(ref _name, value); }
    public bool IsCli { get => _isCli; internal set => SetProperty(ref _isCli, value); }
    public Bitmap? Icon { get => _icon; internal set { if (SetProperty(ref _icon, value)) { OnPropertyChanged(nameof(HasIcon)); OnPropertyChanged(nameof(NoIcon)); } } }
    public bool HasIcon => Icon is not null;
    public bool NoIcon => Icon is null;
}

/// <summary>Shared bounded icon cache. Native extraction runs away from the UI and never affects routing.</summary>
public sealed class ApplicationVisualCatalog(bool preview = false, Func<string, Bitmap?>? iconReader = null) : IDisposable
{
    private readonly object _gate = new();
    private static readonly SemaphoreSlim NativeReads = new(4);
    private readonly Dictionary<string, ProcessVisual> _visuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Bitmap?>> _icons = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, LaunchTarget> _owners = new Dictionary<string, LaunchTarget>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    public void Update(IReadOnlyList<LaunchTarget> targets, EgressProfileDocument profile)
    {
        var owners = new Dictionary<string, LaunchTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets.OrderBy(item => item.Kind == LaunchKind.CliNative ? 0 : 1))
        {
            var paths = ApplicationCatalogProjection.Merge(target, profile.Applications.Where(item => target.SelectionKeys.Contains(item.DiscoveryKey)), EgressRouteTarget.DefaultAdapter).ExecutablePaths;
            foreach (string path in paths) owners.TryAdd(path, target);
        }
        Volatile.Write(ref _owners, owners);
    }
    public ProcessVisual ForApplication(LaunchTarget target) => Get(target.CanonicalExecutable
        ?? target.OwnedExecutables.FirstOrDefault(path => !InstalledApplicationResolver.IsInstaller(path)), target);
    public ProcessVisual ForProcess(string? path) => Get(path, null);
    private ProcessVisual Get(string? path, LaunchTarget? explicitOwner)
    {
        string lookup = path ?? explicitOwner?.DiscoveryKey ?? "unknown";
        var owner = explicitOwner ?? Volatile.Read(ref _owners).GetValueOrDefault(lookup);
        var definition = SupportedApplicationCatalog.ForExecutable(path);
        string name = owner?.Name ?? definition?.Name ?? (path is null ? "未知进程" : Path.GetFileNameWithoutExtension(path));
        string key = owner?.DiscoveryKey ?? lookup;
        lock (_gate)
        {
            bool created = !_visuals.TryGetValue(lookup, out var visual);
            if (created)
            {
                visual = new ProcessVisual(key, name, owner?.Kind == LaunchKind.CliNative);
                if (_disposed || _visuals.Count >= 1024) return visual;
                _visuals.Add(lookup, visual);
                if (!preview && owner is null && definition is null && path is not null)
                    _ = DescribeAsync(path, lookup, visual);
            }
            else if (owner is not null)
            { visual!.Name = name; visual.Key = key; visual.IsCli = owner.Kind == LaunchKind.CliNative; }
            string? iconKey = preview ? owner?.CatalogId ?? definition?.Id ?? lookup : owner?.IconPath ?? path;
            if (_disposed || iconKey is null || visual!.IconKey == iconKey) return visual!;
            visual.IconKey = iconKey;
            if (!_icons.TryGetValue(iconKey, out var task))
            {
                if (_icons.Count >= 512) return visual;
                task = preview ? Task.FromResult(ReadIcon(iconKey)) : ReadNativeAsync(iconKey);
                _icons.Add(iconKey, task);
            }
            if (task.IsCompletedSuccessfully) visual.Icon = task.Result;
            else _ = AssignAsync(task, visual, iconKey);
            return visual;
        }
    }
    private Bitmap? ReadIcon(string path)
    {
        try { return (iconReader ?? (preview ? _ => null : AppIconLoader.Load))(path); }
        catch { return null; } // Artwork failure is not a routing or protection failure.
    }
    private async Task<Bitmap?> ReadNativeAsync(string path)
    {
        await NativeReads.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => ReadIcon(path)).ConfigureAwait(false); }
        finally { NativeReads.Release(); }
    }
    private async Task DescribeAsync(string path, string lookup, ProcessVisual visual)
    {
        string? label;
        await NativeReads.WaitAsync().ConfigureAwait(false);
        try { label = await Task.Run(() => AppIconLoader.Describe(path)).ConfigureAwait(false); }
        catch { return; }
        finally { NativeReads.Release(); }
        if (label is not null)
            Dispatcher.UIThread.Post(() => { if (!_disposed && !Volatile.Read(ref _owners).ContainsKey(lookup)) visual.Name = label; });
    }
    private async Task AssignAsync(Task<Bitmap?> image, ProcessVisual visual, string iconKey)
    {
        Bitmap? bitmap;
        try { bitmap = await image.ConfigureAwait(false); } catch { return; }
        Dispatcher.UIThread.Post(() => { lock (_gate) if (!_disposed && visual.IconKey == iconKey) visual.Icon = bitmap; });
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var task in _icons.Values)
                if (task.IsCompletedSuccessfully) task.Result?.Dispose();
                else _ = task.ContinueWith(done => { if (done.IsCompletedSuccessfully) done.Result?.Dispose(); }, TaskScheduler.Default);
            _icons.Clear(); _visuals.Clear();
        }
    }
}
