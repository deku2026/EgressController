using Avalonia.Media;

namespace EgressController.Presentation;

public static class DesktopFonts
{
    public static FontManagerOptions Options() => new()
    {
        DefaultFamilyName = "Segoe UI",
        FontFallbacks = [new() { FontFamily = new FontFamily("Microsoft YaHei UI") }, new() { FontFamily = new FontFamily("Segoe UI Symbol") }],
    };
}
