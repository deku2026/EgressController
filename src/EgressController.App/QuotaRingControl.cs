using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EgressController.App;

public sealed class QuotaRingControl : Control
{
    public static readonly StyledProperty<double> PercentageProperty =
        AvaloniaProperty.Register<QuotaRingControl, double>(nameof(Percentage));

    public double Percentage
    {
        get => GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PercentageProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        const double stroke = 16;
        double radius = Math.Max(1, size / 2 - stroke);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#E5EAF4")), stroke);
        context.DrawEllipse(null, pen, center, radius, radius);

        double percentage = Math.Clamp(Percentage, 0, 100) / 100d;
        if (percentage <= 0)
            return;
        if (percentage >= 0.999999)
        {
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#4361D7")), stroke), center, radius, radius);
            return;
        }

        double angle = percentage * Math.PI * 2;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(
            center.X + Math.Sin(angle) * radius,
            center.Y - Math.Cos(angle) * radius);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(start, isFilled: false);
            path.ArcTo(end, new Size(radius, radius), 0, percentage > 0.5, SweepDirection.Clockwise);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#4361D7")), stroke), geometry);
    }
}
