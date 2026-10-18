using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EgressController.Guardian;

namespace EgressController.Presentation;

/// <summary>The same original vector shield is used in the panel and notification-area icon.</summary>
public sealed class GuardianGlyph : Control
{
    public static readonly StyledProperty<GuardVisualState> StateProperty = AvaloniaProperty.Register<GuardianGlyph, GuardVisualState>(nameof(State), GuardVisualState.Waiting);
    private static readonly Geometry Shield = Geometry.Parse("M16 2L28 6V15C28 22 23 27 16 30C9 27 4 22 4 15V6Z");
    private static readonly Geometry Check = Geometry.Parse("M9 15L12 12L15 15L22 8L25 11L15 21Z");
    private static readonly Geometry Attention = Geometry.Parse("M14 9H18V18H14Z M14 20H18V24H14Z");
    private static readonly Geometry Pause = Geometry.Parse("M10 14H22V18H10Z");
    static GuardianGlyph() => AffectsRender<GuardianGlyph>(StateProperty);
    public GuardVisualState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public override void Render(DrawingContext context)
    {
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 34;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale));
        var fill = new SolidColorBrush(Color.Parse(State switch { GuardVisualState.Disabled => "#8791A2", GuardVisualState.Unavailable => "#B8384A", GuardVisualState.Waiting => "#7887B3", _ => "#5062CE" }));
        context.DrawGeometry(fill, new Pen(Brushes.White, 1.1), Shield);
        context.DrawGeometry(Brushes.White, null, State == GuardVisualState.Healthy ? Check : State == GuardVisualState.Disabled ? Pause : Attention);
        if (State is not (GuardVisualState.Healthy or GuardVisualState.Disabled))
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#D6455D")), new Pen(Brushes.White, 1.8), new(26, 26), 6.2, 6.2);
    }
}

public static class GuardianIcon
{
    public static WindowIcon Create(GuardVisualState state) { using var data = new MemoryStream(CreateBytes(state)); return new WindowIcon(data); }
    public static byte[] CreateBytes(GuardVisualState state)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64];
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            var glyph = new GuardianGlyph { Width = size, Height = size, State = state };
            glyph.Measure(new(size, size)); glyph.Arrange(new Rect(0, 0, size, size));
            using var bitmap = new RenderTargetBitmap(new(size, size), new(96, 96));
            bitmap.Render(glyph);
            using var png = new MemoryStream(); bitmap.Save(png, new PngBitmapEncoderOptions()); images.Add(png.ToArray());
        }
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var png in images) writer.Write(png);
        return stream.ToArray();
    }
}
