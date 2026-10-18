using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EgressController.App.Views;

/// <summary>Routes view-only actions to the window; no production services are created by a view.</summary>
public class PageView : UserControl
{
    public void OnOpenLogDirectory(object? sender, RoutedEventArgs args) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnOpenLogDirectory(sender, args);
    public void OnOpenGuardLogDirectory(object? sender, RoutedEventArgs args) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnOpenGuardLogDirectory(sender, args);
    public void OnShowDiagnostics(object? sender, RoutedEventArgs args) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnShowDiagnostics(sender, args);
    public void OnCopyDiagnostics(object? sender, RoutedEventArgs args) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnCopyDiagnostics(sender, args);
    public void OnConnectionDoubleTapped(object? sender, TappedEventArgs args) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnConnectionDoubleTapped(sender, args);
}
