using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EgressController.Presentation;

public sealed class AppAvatar : Control
{
    private static readonly IBrush FallbackInk = Brush.Parse("#718099");
    private static readonly IBrush TerminalSurface = Brush.Parse("#263449");
    private static readonly Pen TerminalEdge = new(Brushes.White, 1);
    private static readonly Pen TerminalStroke = new(Brushes.White, 1.2);
    private static readonly Lazy<Geometry> FallbackGeometry = new(() => Icons.For("apps"));
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<AppAvatar, IImage?>(nameof(Source));
    public static readonly StyledProperty<bool> CliProperty = AvaloniaProperty.Register<AppAvatar, bool>(nameof(Cli));
    static AppAvatar() => AffectsRender<AppAvatar>(SourceProperty, CliProperty);
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool Cli { get => GetValue(CliProperty); set => SetValue(CliProperty, value); }
    public override void Render(DrawingContext context)
    {
        double side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 2) return;
        bool dark = IconContrast.NeedsDarkSurface(Source);
        var plate = new Rect(.5, .5, side - 1, side - 1);
        context.DrawRectangle(dark ? IconContrast.DarkSurface : IconContrast.LightSurface,
            dark ? IconContrast.DarkEdge : IconContrast.LightEdge, plate, side * .23, side * .23);
        if (Source is { } image)
        {
            double padding = side >= 28 ? 4 : 2.5;
            double scale = (side - 2 * padding) / Math.Max(image.Size.Width, image.Size.Height);
            double width = image.Size.Width * scale, height = image.Size.Height * scale;
            context.DrawImage(image, new Rect(image.Size), new Rect((side - width) / 2, (side - height) / 2, width, height));
        }
        else
        {
            using var transform = context.PushTransform(Matrix.CreateScale((side - 8) / 23, (side - 8) / 23) * Matrix.CreateTranslation(4, 4));
            context.DrawGeometry(FallbackInk, null, FallbackGeometry.Value);
        }
        if (Cli)
        {
            double badgeScale = Math.Min(1, side / 32);
            using var transform = context.PushTransform(Matrix.CreateScale(badgeScale, badgeScale) * Matrix.CreateTranslation(side * (1 - badgeScale), side * (1 - badgeScale)));
            var box = new Rect(side - 16, side - 12, 16, 12);
            context.DrawRectangle(TerminalSurface, TerminalEdge, box, 3, 3);
            context.DrawLine(TerminalStroke, new(side - 12, side - 9), new(side - 9, side - 6));
            context.DrawLine(TerminalStroke, new(side - 9, side - 6), new(side - 12, side - 3));
            context.DrawLine(TerminalEdge, new(side - 7, side - 3), new(side - 3, side - 3));
        }
    }
}
