using Avalonia.Media;

namespace EgressController.Presentation;

/// <summary>Stable exit colors, shared by monthly totals, application rows and daily columns.</summary>
public static class TrafficPalette
{
    public static readonly Color HomeColor = Color.Parse("#7766D4");
    public static readonly Color OtherColor = Color.Parse("#3E9BAE");
    public static readonly IBrush Home = new SolidColorBrush(HomeColor);
    public static readonly IBrush Other = new SolidColorBrush(OtherColor);
    public static readonly IBrush Unknown = Brush.Parse("#9AA4B4");
    public static readonly IBrush Track = Brush.Parse("#EEF0F5");
    public static readonly IBrush Muted = Brush.Parse("#687488");
}
