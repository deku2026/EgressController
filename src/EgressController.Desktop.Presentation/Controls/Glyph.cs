using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace EgressController.Desktop.Presentation.Controls;

public sealed class Glyph : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<Glyph, string>(nameof(Kind), "overview");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<Glyph, IBrush?>(nameof(Foreground), Brush.Parse("#788398"));
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static Glyph() => AffectsRender<Glyph>(KindProperty, ForegroundProperty);
    public override void Render(DrawingContext context)
    {
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale));
        var pen = new Pen(Foreground, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (Kind)
        {
            case "overview":
                context.DrawRectangle(null, pen, new Rect(3, 3, 7, 7), 2, 2); context.DrawRectangle(null, pen, new Rect(14, 3, 7, 7), 2, 2); context.DrawRectangle(null, pen, new Rect(3, 14, 7, 7), 2, 2); context.DrawRectangle(null, pen, new Rect(14, 14, 7, 7), 2, 2); break;
            case "apps":
                context.DrawRectangle(null, pen, new Rect(3, 4, 18, 14), 2, 2); context.DrawLine(pen, new(9, 21), new(15, 21)); context.DrawLine(pen, new(12, 18), new(12, 21)); context.DrawLine(pen, new(3, 8), new(21, 8)); break;
            case "rules":
                for (int y = 5; y <= 19; y += 7) { context.DrawEllipse(null, pen, new Point(5, y), 1.4, 1.4); context.DrawLine(pen, new(10, y), new(21, y)); }
                break;
            case "nodes":
                context.DrawEllipse(null, pen, new Point(12, 5), 3, 3); context.DrawEllipse(null, pen, new Point(5, 19), 3, 3); context.DrawEllipse(null, pen, new Point(19, 19), 3, 3); context.DrawLine(pen, new(10, 8), new(6, 16)); context.DrawLine(pen, new(14, 8), new(18, 16)); context.DrawLine(pen, new(8, 19), new(16, 19)); break;
            case "settings":
                context.DrawEllipse(null, pen, new Point(12, 12), 7, 7); context.DrawEllipse(null, pen, new Point(12, 12), 2.5, 2.5);
                for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; context.DrawLine(pen, new(12 + 8 * Math.Cos(a), 12 + 8 * Math.Sin(a)), new(12 + 10 * Math.Cos(a), 12 + 10 * Math.Sin(a))); }
                break;
            case "shield":
                DrawPath(context, pen, "M12 2 L21 6 L20 14 Q18 20 12 23 Q6 20 4 14 L3 6 Z"); DrawPath(context, pen, "M7 12 L10.5 15.5 L17 9"); break;
            case "network":
                context.DrawRectangle(null, pen, new Rect(3, 7, 18, 12), 2, 2); for (int x = 7; x <= 17; x += 5) context.DrawEllipse(Foreground, null, new Point(x, 15), .8, .8); DrawPath(context, pen, "M7 7 L7 3 M17 7 L17 3"); break;
            case "arrow": DrawPath(context, pen, "M4 12 L20 12 M14 6 L20 12 L14 18"); break;
            case "search": context.DrawEllipse(null, pen, new Point(10, 10), 6, 6); context.DrawLine(pen, new(15, 15), new(21, 21)); break;
            case "logs": context.DrawRectangle(null, pen, new Rect(5, 3, 14, 18), 2, 2); DrawPath(context, pen, "M9 8 L15 8 M9 12 L15 12 M9 16 L13 16"); break;
            case "plus": DrawPath(context, pen, "M5 12 L19 12 M12 5 L12 19"); break;
            case "gauge": DrawPath(context, pen, "M4 19 A10 10 0 1 1 20 19 M12 12 L17 7 M5 14 L6 14 M12 4 L12 5 M18 14 L19 14"); context.DrawEllipse(Foreground, null, new Point(12, 12), 1.8, 1.8); break;
            case "sliders": DrawPath(context, pen, "M3 7 L7 7 M11 7 L21 7 M3 17 L13 17 M17 17 L21 17"); context.DrawEllipse(null, pen, new Point(9,7), 2,2); context.DrawEllipse(null,pen,new Point(15,17),2,2); break;
            case "refresh": DrawPath(context, pen, "M20 9 A8 8 0 1 0 20 16 M20 3 L20 9 L14 9"); break;
            case "chevron-down": DrawPath(context, pen, "M5 9 L12 16 L19 9"); break;
            case "chevron-up": DrawPath(context, pen, "M5 15 L12 8 L19 15"); break;
            case "check": DrawPath(context, pen, "M5 12 L10 17 L20 6"); break;
            case "globe": context.DrawEllipse(null, pen, new Point(12, 12), 9, 9); context.DrawEllipse(null, pen, new Point(12, 12), 4, 9); context.DrawLine(pen, new(3, 12), new(21, 12)); break;
            default: context.DrawRectangle(null, pen, new Rect(4, 4, 16, 16), 4, 4); break;
        }
    }
    private static readonly Dictionary<string, Geometry> Paths = new(StringComparer.Ordinal);
    private static void DrawPath(DrawingContext context, IPen pen, string data)
    {
        if (!Paths.TryGetValue(data, out var geometry)) Paths[data] = geometry = Geometry.Parse(data);
        context.DrawGeometry(null, pen, geometry);
    }
}
