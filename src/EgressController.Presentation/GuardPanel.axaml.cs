using Avalonia.Controls;
using Avalonia.Interactivity;
using EgressController.Guardian;

namespace EgressController.Presentation;

public sealed partial class GuardPanel : UserControl
{
    public event Action<string>? OpenDirectoryRequested;
    public GuardPanel()
    {
        InitializeComponent();
        var details = this.FindControl<Expander>("ServiceDetails")!;
        details.PropertyChanged += (_, change) =>
        {
            if (change.Property == Expander.IsExpandedProperty && details.IsExpanded)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<Button>("InstallServiceButton")?.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
        };
    }
    private void OpenServiceLogs(object? sender, RoutedEventArgs e)
    { if (DataContext is GuardManagementViewModel vm) OpenDirectoryRequested?.Invoke(vm.LogDirectory); }
    private void OpenManagementLogs(object? sender, RoutedEventArgs e)
    { if (DataContext is GuardManagementViewModel vm && Path.GetDirectoryName(vm.DiagnosticLogPath) is { } path) OpenDirectoryRequested?.Invoke(path); }
}
