using Avalonia.Interactivity;
using Avalonia.VisualTree;
namespace EgressController.App.Views;
public sealed partial class OverviewPage : PageView
{
    public OverviewPage() => InitializeComponent();
    private void OnShowTraffic(object? sender, RoutedEventArgs e) => this.FindAncestorOfType<MainWindow>()?.SelectPage("traffic");
}
