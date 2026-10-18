using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace EgressController.Presentation;

public sealed partial class DesktopTheme : Styles
{
    public DesktopTheme() => AvaloniaXamlLoader.Load(this);
}
