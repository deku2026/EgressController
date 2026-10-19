using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EgressController.Diagnostics;

namespace EgressController.Presentation;

public sealed record DailyTrafficPoint(DateOnly Day, long Home, long Other, long Unknown)
{
    public double Total => (double)Home + Other + Unknown;
}

public sealed class DailyTrafficChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<DailyTrafficPoint>> DaysProperty = AvaloniaProperty.Register<DailyTrafficChart, IReadOnlyList<DailyTrafficPoint>>(nameof(Days), []);
    public IReadOnlyList<DailyTrafficPoint> Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    static DailyTrafficChart() => AffectsRender<DailyTrafficChart>(DaysProperty);
    public override void Render(DrawingContext context)
    {
        if (Days.Count == 0 || Bounds.Width < 40 || Bounds.Height < 50) return;
        double peak = TrafficUnits.ScaleCeiling(Days.Select(day => day.Total).Max());
        var plot = new Rect(0, 23, Bounds.Width, Bounds.Height - 45);
        var rule = new Pen(Brush.Parse("#E9EDF3"), 1, new DashStyle([2, 4], 0));
        for (int i = 0; i < 3; i++)
            context.DrawLine(rule, new(0, plot.Top + plot.Height * i / 2), new(plot.Right, plot.Top + plot.Height * i / 2));
        Text(context, TrafficUnits.Bytes(peak), new(Bounds.Width - 84, 0));
        double step = plot.Width / Days.Count, width = Math.Clamp(step * .57, 3, 18);
        for (int i = 0; i < Days.Count; i++)
        {
            var day = Days[i]; double height = plot.Height * day.Total / peak;
            if (height <= 0) continue;
            var bar = new Rect(i * step + (step - width) / 2, plot.Bottom - height, width, height);
            using (context.PushClip(new RoundedRect(bar, 3)))
            {
                double bottom = plot.Bottom;
                foreach (var (value, brush) in new[] { (day.Home, TrafficPalette.Home), (day.Other, TrafficPalette.Other), (day.Unknown, TrafficPalette.Unknown) })
                {
                    double h = plot.Height * value / peak;
                    if (h > 0) context.DrawRectangle(brush, null, new Rect(bar.X, bottom - h, width, h));
                    bottom -= h;
                }
            }
        }
        Text(context, Days[0].Day.ToString("MM.dd"), new(0, plot.Bottom + 7));
        Text(context, Days[^1].Day.ToString("MM.dd"), new(Bounds.Width - 34, plot.Bottom + 7));
    }
    private static void Text(DrawingContext context, string text, Point position) => context.DrawText(new FormattedText(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("fonts:Inter#Inter"), 11, TrafficPalette.Muted), position);
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Days.Count == 0) return;
        var day = Days[Math.Clamp((int)(e.GetPosition(this).X / Math.Max(1, Bounds.Width) * Days.Count), 0, Days.Count - 1)];
        ToolTip.SetTip(this, day.Total == 0 ? $"{day.Day:MM.dd} · 无用量记录" : $"{day.Day:MM.dd} · 家宽 {TrafficUnits.Bytes(day.Home)} · 非家宽 {TrafficUnits.Bytes(day.Other)}"
            + (day.Unknown > 0 ? " · 未识别 " + TrafficUnits.Bytes(day.Unknown) : ""));
    }
}
