using Avalonia.Markup.Xaml;
using Avalonia.Styling;
namespace EgressController.Desktop.Presentation;

public partial class MacTheme : Styles
{
    public MacTheme() => AvaloniaXamlLoader.Load(this);
}
