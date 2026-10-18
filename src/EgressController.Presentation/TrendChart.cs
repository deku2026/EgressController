using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EgressController.Diagnostics;

namespace EgressController.Presentation;

/// <summary>Edge-to-edge sparkline: real timestamps, visible gaps and the same zero-based scale in both directions.</summary>
public sealed class TrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<RatePoint>> SamplesProperty = AvaloniaProperty.Register<TrendChart, IReadOnlyList<RatePoint>>(nameof(Samples), Array.Empty<RatePoint>());
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<TrendChart, double>(nameof(Maximum), 1024);
    public static readonly StyledProperty<IBrush> StrokeProperty = AvaloniaProperty.Register<TrendChart, IBrush>(nameof(Stroke), Brush.Parse("#728BAD"));
    static TrendChart() => AffectsRender<TrendChart>(SamplesProperty, StrokeProperty, MaximumProperty);
    public IReadOnlyList<RatePoint> Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private Rect Plot => new(0, 24, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height - 24));
    public static Point Project(RatePoint sample, Rect plot, double maximum) => new(
        plot.Right - Math.Clamp(sample.SecondsAgo / 60, 0, 1) * plot.Width,
        plot.Bottom - Math.Clamp((sample.Value ?? 0) / Math.Max(1, maximum), 0, 1) * plot.Height);

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 90 || Bounds.Height < 40) return;
        var plot = Plot;
        double maximum = double.IsFinite(Maximum) && Maximum > 0 ? Maximum : 1024;
        var label = new FormattedText(TrafficUnits.Rate(maximum), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("fonts:Inter#Inter"), 10.5, TrafficPalette.Muted);
        context.DrawText(label, new(Bounds.Width - label.Width - 14, 2));
        var gridLine = new Pen(Brush.Parse("#EDF0F5"), 1, new DashStyle([2, 5], 0));
        context.DrawLine(gridLine, new(0, plot.Top), new(plot.Right, plot.Top));
        context.DrawLine(gridLine, new(0, plot.Top + plot.Height / 2), new(plot.Right, plot.Top + plot.Height / 2));
        var pen = new Pen(Stroke, 2) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var segment = new List<Point>();
        double? previousAge = null;
        void DrawSegment()
        {
            if (segment.Count < 2) { segment.Clear(); return; }
            var line = new StreamGeometry();
            using (var geometry = line.Open()) { geometry.BeginFigure(segment[0], false); foreach (var point in segment.Skip(1)) geometry.LineTo(point); geometry.EndFigure(false); }
            var fill = new StreamGeometry();
            using (var geometry = fill.Open())
            {
                geometry.BeginFigure(new(segment[0].X, plot.Bottom), true);
                foreach (var point in segment) geometry.LineTo(point);
                geometry.LineTo(new(segment[^1].X, plot.Bottom)); geometry.EndFigure(true);
            }
            Color color = (Stroke as ISolidColorBrush)?.Color ?? Color.Parse("#7766D4");
            var wash = new LinearGradientBrush { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(0, 1, RelativeUnit.Relative),
                GradientStops = [new(Color.FromArgb(32, color.R, color.G, color.B), 0), new(Color.FromArgb(6, color.R, color.G, color.B), 1)] };
            context.DrawGeometry(wash, null, fill); context.DrawGeometry(null, pen, line); segment.Clear();
        }
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            foreach (var point in Samples)
            {
                if (point.Value is not { } value || !double.IsFinite(value) || value < 0 || point.SecondsAgo is < 0 or > 60)
                { DrawSegment(); previousAge = null; continue; }
                if (previousAge is { } age && (age - point.SecondsAgo <= 0 || age - point.SecondsAgo > 3)) DrawSegment();
                segment.Add(Project(point, plot, maximum)); previousAge = point.SecondsAgo;
            }
            DrawSegment();
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var plot = Plot;
        double age = Math.Clamp((plot.Right - e.GetPosition(this).X) / plot.Width * 60, 0, 60);
        var nearest = Samples.OrderBy(point => Math.Abs(point.SecondsAgo - age)).FirstOrDefault();
        ToolTip.SetTip(this, nearest is null ? "尚无采样" : $"{nearest.SecondsAgo:0} 秒前 · {TrafficUnits.Rate(nearest.Value)}");
    }
}
