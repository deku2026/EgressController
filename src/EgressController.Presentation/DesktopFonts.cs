using Avalonia.Media;

namespace EgressController.Presentation;

public static class DesktopFonts
{
    public static FontManagerOptions Options() => new()
    {
        DefaultFamilyName = "fonts:Inter#Inter",
        FontFallbacks = OperatingSystem.IsMacOS()
            ? [new() { FontFamily = new FontFamily("PingFang SC") }, new() { FontFamily = new FontFamily("Apple Symbols") }]
            : [new() { FontFamily = new FontFamily("Microsoft YaHei UI") }, new() { FontFamily = new FontFamily("Segoe UI Symbol") }],
    };
}
