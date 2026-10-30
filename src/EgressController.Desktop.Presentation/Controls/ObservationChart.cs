using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace EgressController.Desktop.Presentation.Controls;

public sealed record Observation(DateTimeOffset At, double? DownloadMiB, double? UploadMiB);
/// <summary>A fixed observation series. UI refresh never creates samples; gaps remain gaps.</summary>
public sealed class ObservationChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<Observation>> SamplesProperty = AvaloniaProperty.Register<ObservationChart, IReadOnlyList<Observation>>(nameof(Samples), Array.Empty<Observation>());
    public IReadOnlyList<Observation> Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    static ObservationChart() => AffectsRender<ObservationChart>(SamplesProperty);
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 120 || Bounds.Height < 50 || Samples.Count < 2) return;
        var plot = new Rect(33, 9, Bounds.Width - 42, Bounds.Height - 31); const double max = 8;
        var grid = new Pen(Brush.Parse("#E9EDF4"), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - plot.Height * i / 4; context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, (i * 2).ToString(CultureInfo.InvariantCulture), new Point(0, y - 5), 9, "#96A0B1");
        }
        var first = Samples[0].At; var last = Samples[^1].At; double span = Math.Max(1, (last - first).TotalSeconds);
        for (int i = 0; i < 3; i++) { var time = first.AddSeconds(span * i / 2); DrawText(context, time.ToString("HH:mm", CultureInfo.InvariantCulture), new Point(plot.Left + (plot.Width - 24) * i / 2, plot.Bottom + 10), 9, "#96A0B1"); }
        foreach (bool download in new[] { true, false })
        {
            var color = download ? Color.Parse("#5D8FE5") : Color.Parse("#A08BD5"); var pen = new Pen(new SolidColorBrush(color), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var points = new List<Point>(); DateTimeOffset? previous = null;
            void Flush()
            {
                if (points.Count < 2) { points.Clear(); return; }
                var path = new StreamGeometry(); using (var g = path.Open()) { g.BeginFigure(points[0], false); foreach (var p in points.Skip(1)) g.LineTo(p); g.EndFigure(false); }
                if (download) { var fill = new StreamGeometry(); using (var g = fill.Open()) { g.BeginFigure(new Point(points[0].X, plot.Bottom), true); foreach (var p in points) g.LineTo(p); g.LineTo(new Point(points[^1].X, plot.Bottom)); g.EndFigure(true); } context.DrawGeometry(new SolidColorBrush(Color.FromArgb(19, color.R, color.G, color.B)), null, fill); }
                context.DrawGeometry(null, pen, path); points.Clear();
            }
            foreach (var sample in Samples)
            {
                double? value = download ? sample.DownloadMiB : sample.UploadMiB;
                if (value is null || value < 0 || !double.IsFinite(value.Value)) { Flush(); previous = null; continue; }
                if (previous is { } at && (sample.At - at).TotalSeconds > 75) Flush();
                points.Add(new Point(plot.Left + (sample.At - first).TotalSeconds / span * plot.Width, plot.Bottom - Math.Clamp(value.Value, 0, max) / max * plot.Height)); previous = sample.At;
            }
            Flush();
        }
    }
    private static void DrawText(DrawingContext c, string text, Point p, double size, string color) => c.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("fonts:Inter#Inter"), size, Brush.Parse(color)), p);
}
