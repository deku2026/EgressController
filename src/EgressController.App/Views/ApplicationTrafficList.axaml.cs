using Avalonia.Controls;
namespace EgressController.App.Views;
public sealed partial class ApplicationTrafficList : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);
    public ApplicationTrafficList() => InitializeComponent();
}
