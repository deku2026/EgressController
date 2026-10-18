using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EgressController.App.ViewModels;

namespace EgressController.App.Views;

public sealed partial class ApplicationsPage : PageView
{
    public ApplicationsPage() => InitializeComponent();
    private async void OnAddExecutable(object? sender, RoutedEventArgs e) => await PickAsync(false);
    private async void OnAddDirectory(object? sender, RoutedEventArgs e) => await PickAsync(true);
    private async void OnLocateDirectory(object? sender, RoutedEventArgs e)
        => await PickAsync(true, (sender as Control)?.DataContext as AppEntryViewModel);
    private async Task PickAsync(bool directory, AppEntryViewModel? entry = null)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            if (vm.IsPreview) { await vm.Apps.AddManualAsync(@"C:\Preview\Manual\app.exe", entry?.DiscoveryKey); return; }
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;
            string? path;
            if (directory)
            {
                var folders = await storage.OpenFolderPickerAsync(new() { Title = "选择应用安装目录", AllowMultiple = false });
                path = folders.FirstOrDefault()?.TryGetLocalPath();
            }
            else
            {
                var files = await storage.OpenFilePickerAsync(new() { Title = "选择主程序 EXE", AllowMultiple = false,
                    FileTypeFilter = [new("Windows 应用") { Patterns = ["*.exe"] }] });
                path = files.FirstOrDefault()?.TryGetLocalPath();
            }
            if (path is not null) await vm.Apps.AddManualAsync(path, entry?.DiscoveryKey);
        }
        catch (Exception error) { vm.Controller.ReportFailure("添加应用", "apps.pick", error.Message, error); }
    }
}
