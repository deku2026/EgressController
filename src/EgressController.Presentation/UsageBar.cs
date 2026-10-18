using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EgressController.Presentation;

public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> HomeProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Home));
    public static readonly StyledProperty<double> OtherProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Other));
    public static readonly StyledProperty<double> UnknownProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Unknown));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Maximum));
    public double Home { get => GetValue(HomeProperty); set => SetValue(HomeProperty, value); }
    public double Other { get => GetValue(OtherProperty); set => SetValue(OtherProperty, value); }
    public double Unknown { get => GetValue(UnknownProperty); set => SetValue(UnknownProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    static UsageBar() => AffectsRender<UsageBar>(HomeProperty, OtherProperty, UnknownProperty, MaximumProperty);
    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size); double radius = Bounds.Height / 2;
        context.DrawRectangle(TrafficPalette.Track, null, rect, radius, radius);
        double home = Positive(Home), other = Positive(Other), unknown = Positive(Unknown), total = home + other + unknown;
        double max = Math.Max(total, Positive(Maximum));
        if (max <= 0) return;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, Bounds.Width * total / max, Bounds.Height), radius)))
        {
            double x = 0;
            foreach (var (value, brush) in new[] { (home, TrafficPalette.Home), (other, TrafficPalette.Other), (unknown, TrafficPalette.Unknown) })
            {
                double width = Bounds.Width * value / max;
                if (width > 0) context.DrawRectangle(brush, null, new Rect(x, 0, width, Bounds.Height));
                x += width;
            }
        }
    }
    private static double Positive(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
