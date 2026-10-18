using Avalonia.Controls;
using Avalonia.Interactivity;
using EgressController.Guardian;

namespace EgressController.Presentation;

public sealed partial class GuardPanel : UserControl
{
    public event Action<string>? OpenDirectoryRequested;
    public GuardPanel() => InitializeComponent();
    private void OpenServiceLogs(object? sender, RoutedEventArgs e)
    { if (DataContext is GuardManagementViewModel vm) OpenDirectoryRequested?.Invoke(vm.LogDirectory); }
    private void OpenManagementLogs(object? sender, RoutedEventArgs e)
    { if (DataContext is GuardManagementViewModel vm && Path.GetDirectoryName(vm.DiagnosticLogPath) is { } path) OpenDirectoryRequested?.Invoke(path); }
}
