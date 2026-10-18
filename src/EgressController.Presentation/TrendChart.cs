using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EgressController.Diagnostics;

namespace EgressController.Presentation;

/// <summary>Timestamped, zero-based chart. Both directions receive the same explicit scale.</summary>
public sealed class TrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<RatePoint>> SamplesProperty = AvaloniaProperty.Register<TrendChart, IReadOnlyList<RatePoint>>(nameof(Samples), Array.Empty<RatePoint>());
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<TrendChart, double>(nameof(Maximum), 1024);
    public static readonly StyledProperty<IBrush> StrokeProperty = AvaloniaProperty.Register<TrendChart, IBrush>(nameof(Stroke), new SolidColorBrush(Color.Parse("#5062CE")));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#788397"));
    private static readonly Pen GridLine = new(new SolidColorBrush(Color.Parse("#E8EDF4")), 1);
    static TrendChart() => AffectsRender<TrendChart>(SamplesProperty, StrokeProperty, MaximumProperty);
    public IReadOnlyList<RatePoint> Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private Rect Plot => new(67, 9, Math.Max(1, Bounds.Width - 74), Math.Max(1, Bounds.Height - 31));
    public static Point Project(RatePoint sample, Rect plot, double maximum) => new(
        plot.Right - Math.Clamp(sample.SecondsAgo / 60, 0, 1) * plot.Width,
        plot.Bottom - Math.Clamp((sample.Value ?? 0) / Math.Max(1, maximum), 0, 1) * plot.Height);

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 90 || Bounds.Height < 40) return;
        var plot = Plot;
        double maximum = double.IsFinite(Maximum) && Maximum > 0 ? Maximum : 1024;
        void Text(string value, Point at) => context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, Muted), at);
        for (int i = 0; i <= 2; i++)
        {
            double y = plot.Top + plot.Height * i / 2;
            context.DrawLine(GridLine, new(plot.Left, y), new(plot.Right, y));
            Text(i == 2 ? "0" : TrafficUnits.Rate(maximum * (2 - i) / 2), new(0, y - 7));
        }
        Text("60 秒前", new(plot.Left, plot.Bottom + 6));
        Text("现在", new(plot.Right - 22, plot.Bottom + 6));
        var pen = new Pen(Stroke, 2);
        RatePoint? previous = null;
        foreach (var point in Samples)
        {
            if (point.Value is not { } value || !double.IsFinite(value) || value < 0 || point.SecondsAgo is < 0 or > 60)
            { previous = null; continue; }
            if (previous is not null && previous.SecondsAgo - point.SecondsAgo is > 0 and <= 3)
                context.DrawLine(pen, Project(previous, plot, maximum), Project(point, plot, maximum));
            previous = point;
        }
        if (Samples.LastOrDefault() is { SecondsAgo: <= 3, Value: { } last } endpoint && double.IsFinite(last))
            context.DrawEllipse(Stroke, null, Project(endpoint, plot, maximum), 2.8, 2.8);
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
