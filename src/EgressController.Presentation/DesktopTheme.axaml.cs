using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace EgressController.Presentation;

public sealed partial class DesktopTheme : Styles
{
    public DesktopTheme() => AvaloniaXamlLoader.Load(this);
    public static Color Accent => Color.Parse("#5062CE");
    public static FluentTheme CreateBaseTheme()
    {
        var theme = new FluentTheme();
        theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Accent };
        return theme;
    }
}
