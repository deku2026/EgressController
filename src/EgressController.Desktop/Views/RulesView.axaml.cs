using Avalonia.Controls;
using EgressController.Desktop.Presentation.Controls;
namespace EgressController.Desktop.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();
        OverlayFocus.Attach(this.FindControl<Border>("RuleCatalogPanel")!, this.FindControl<Control>("RuleCatalogSearch")!);
        OverlayFocus.Attach(this.FindControl<Border>("CustomRulePanel")!, this.FindControl<Control>("CustomRuleNameBox")!);
    }
}
