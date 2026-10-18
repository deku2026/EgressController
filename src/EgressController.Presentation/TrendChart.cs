using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EgressController.Presentation;

public sealed class TrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>> SamplesProperty = AvaloniaProperty.Register<TrendChart, IReadOnlyList<double>>(nameof(Samples), Array.Empty<double>());
    public static readonly StyledProperty<IBrush> StrokeProperty = AvaloniaProperty.Register<TrendChart, IBrush>(nameof(Stroke), new SolidColorBrush(Color.Parse("#5062CE")));
    static TrendChart() => AffectsRender<TrendChart>(SamplesProperty, StrokeProperty);
    public IReadOnlyList<double> Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        var grid = new Pen(new SolidColorBrush(Color.Parse("#E8EDF4")), 1);
        for (int i = 1; i <= 3; i++) context.DrawLine(grid, new(0, height * i / 3), new(width, height * i / 3));
        if (Samples.Count < 2) return;
        double maximum = Math.Max(1, Samples.Where(double.IsFinite).DefaultIfEmpty(1).Max() * 1.25);
        Point PointAt(int i) => new(width * i / (Samples.Count - 1), height - 4 - Math.Clamp(double.IsFinite(Samples[i]) ? Samples[i] / maximum : 0, 0, 1) * (height - 12));
        var line = new StreamGeometry();
        using (var drawing = line.Open())
        {
            drawing.BeginFigure(PointAt(0), false);
            for (int i = 1; i < Samples.Count; i++) drawing.LineTo(PointAt(i));
            drawing.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Stroke, 2.4), line);
        context.DrawEllipse(Stroke, null, PointAt(Samples.Count - 1), 3, 3);
    }
}
