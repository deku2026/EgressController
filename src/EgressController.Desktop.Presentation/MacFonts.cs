using Avalonia.Media;
namespace EgressController.Desktop.Presentation;

public static class MacFonts
{
    public static FontManagerOptions Options() => new()
    {
        DefaultFamilyName = "fonts:Inter#Inter",
        FontFallbacks = [new() { FontFamily = new FontFamily("PingFang SC") }, new() { FontFamily = new FontFamily("Microsoft YaHei UI") }, new() { FontFamily = new FontFamily("Noto Sans CJK SC") }, new() { FontFamily = new FontFamily("Arial Unicode MS") }],
    };
}
