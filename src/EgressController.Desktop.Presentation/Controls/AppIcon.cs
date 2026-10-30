using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace EgressController.Desktop.Presentation.Controls;

public sealed class AppIcon : Control
{
    public override void Render(DrawingContext context)
    {
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 1024; using var transform = context.PushTransform(Matrix.CreateScale(scale, scale));
        var background = new LinearGradientBrush { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(1, 1, RelativeUnit.Relative), GradientStops = [new(Color.Parse("#4A72B5"), 0), new(Color.Parse("#27436D"), .6), new(Color.Parse("#182A46"), 1)] };
        context.DrawRectangle(background, new Pen(Brush.Parse("#718DB5"), 2), new Rect(64, 64, 896, 896), 200, 200);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#124B9AF2")), null, new Point(763, 263), 248, 248);
        var branch = new Pen(Brush.Parse("#D6E4FC"), 49, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, branch, Geometry.Parse("M320 341 L430 341 Q487 341 487 398 L487 448 Q487 512 554 512"));
        context.DrawGeometry(null, branch, Geometry.Parse("M320 683 L430 683 Q487 683 487 626 L487 576 Q487 512 554 512"));
        var main = new Pen(Brushes.White, 60, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawLine(main, new Point(320, 512), new Point(746, 512));
        context.DrawGeometry(null, main, Geometry.Parse("M638 399 L752 512 L638 625"));
        foreach (var item in new[] { (341d, "#BEA0EE"), (512d, "#67CCD4"), (683d, "#8CB7FB") }) context.DrawEllipse(Brush.Parse(item.Item2), new Pen(Brush.Parse("#25446D"), 12), new Point(285, item.Item1), 39, 39);
    }
}
