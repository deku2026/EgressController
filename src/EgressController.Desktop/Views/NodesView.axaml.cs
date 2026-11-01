using Avalonia.Controls;
using EgressController.Desktop.Presentation.Controls;
namespace EgressController.Desktop.Views;

public partial class NodesView : UserControl
{
    public NodesView()
    {
        InitializeComponent();
        OverlayFocus.Attach(this.FindControl<Border>("GroupManagementPanel")!, this.FindControl<Control>("FinishGroupManagementButton")!);
    }
}
