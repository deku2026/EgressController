using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EgressController.Presentation;

public sealed class AppAvatar : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<AppAvatar, IImage?>(nameof(Source));
    public static readonly StyledProperty<bool> CliProperty = AvaloniaProperty.Register<AppAvatar, bool>(nameof(Cli));
    static AppAvatar() => AffectsRender<AppAvatar>(SourceProperty, CliProperty);
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool Cli { get => GetValue(CliProperty); set => SetValue(CliProperty, value); }
    public override void Render(DrawingContext context)
    {
        double side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 2) return;
        if (Source is { } image) context.DrawImage(image, new Rect(0, 0, image.Size.Width, image.Size.Height), new Rect(1, 1, side - 2, side - 2));
        else
        {
            using var transform = context.PushTransform(Matrix.CreateScale(side / 23, side / 23));
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#718099")), null, Icons.For("apps"));
        }
        if (Cli)
        {
            var box = new Rect(side - 16, side - 12, 16, 12);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#263449")), new Pen(Brushes.White, 1), box, 3, 3);
            context.DrawLine(new Pen(Brushes.White, 1.2), new(side - 12, side - 9), new(side - 9, side - 6));
            context.DrawLine(new Pen(Brushes.White, 1.2), new(side - 9, side - 6), new(side - 12, side - 3));
            context.DrawLine(new Pen(Brushes.White, 1), new(side - 7, side - 3), new(side - 3, side - 3));
        }
    }
}
