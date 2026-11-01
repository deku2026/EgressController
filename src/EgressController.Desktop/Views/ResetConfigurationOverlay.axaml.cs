using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace EgressController.Desktop.Views;

public partial class ResetConfigurationOverlay : UserControl
{
    public ResetConfigurationOverlay()
    {
        InitializeComponent();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty && IsVisible)
                Dispatcher.UIThread.Post(() => { if (IsVisible) this.FindControl<Button>("CancelResetButton")?.Focus(NavigationMethod.Tab); });
        };
    }
}
