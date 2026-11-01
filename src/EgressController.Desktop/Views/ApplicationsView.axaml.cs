using Avalonia.Controls;
using EgressController.Desktop.Presentation.Controls;
namespace EgressController.Desktop.Views;

public partial class ApplicationsView : UserControl
{
    public ApplicationsView()
    {
        InitializeComponent(); ApplicationArtworkRequests.Attach(this);
        OverlayFocus.Attach(this.FindControl<Border>("AppCatalogPanel")!, this.FindControl<Control>("CatalogSearch")!);
    }
}
