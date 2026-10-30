using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
namespace EgressController.Desktop.Presentation.Controls;
/// <summary>Shared adaptive backing; original product artwork retains its color and aspect ratio.</summary>
public sealed class ProductAvatar : Control
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    public static readonly StyledProperty<IImage?> SourceProperty=AvaloniaProperty.Register<ProductAvatar,IImage?>(nameof(Source));
    public IImage? Source{get=>GetValue(SourceProperty);set=>SetValue(SourceProperty,value);}
    public static readonly StyledProperty<string> ProductProperty = AvaloniaProperty.Register<ProductAvatar, string>(nameof(Product), "app");
    public string Product { get => GetValue(ProductProperty); set => SetValue(ProductProperty, value); }
    static ProductAvatar() => AffectsRender<ProductAvatar>(ProductProperty,SourceProperty);
    public static Bitmap? Artwork(string product)
    {
        if (Cache.TryGetValue(product, out var value)) return value;
        if(string.IsNullOrEmpty(product)||product.Length>64||!product.All(character=>char.IsAsciiLetterOrDigit(character)||character is '-' or '_'))return null;
        var uri = new Uri($"avares://EgressController.Desktop.Presentation/Assets/{product}.png");
        try { value = AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null; } catch { value = null; }
        if(value is not null)Cache[product] = value; return value;
    }
    public override void Render(DrawingContext context)
    {
        double side = Math.Min(Bounds.Width, Bounds.Height); if (side < 2) return;
        var artwork = Source??Artwork(Product); bool dark = ArtworkContrast.NeedsDarkSurface(artwork);
        context.DrawRectangle(dark ? ArtworkContrast.DarkSurface : ArtworkContrast.LightSurface, dark ? ArtworkContrast.DarkEdge : ArtworkContrast.LightEdge, new Rect(.5, .5, side - 1, side - 1), side * .24, side * .24);
        if (artwork is not null)
        {
            double scale = (side - 9) / Math.Max(artwork.Size.Width, artwork.Size.Height);
            double w = artwork.Size.Width * scale, h = artwork.Size.Height * scale;
            context.DrawImage(artwork, new Rect(artwork.Size), new Rect((side - w) / 2, (side - h) / 2, w, h));
        }
        else if (Product == "domain")
        {
            var pen = new Pen(Brush.Parse("#8295B5"), 1.2); var center = new Point(side / 2, side / 2); double r = side * .27;
            context.DrawEllipse(null, pen, center, r, r); context.DrawEllipse(null, pen, center, r * .45, r); context.DrawLine(pen, new Point(center.X - r, center.Y), new Point(center.X + r, center.Y));
        }
        else
        {
            var text = new FormattedText(Product is "custom" ? "IP" : Product is "china" ? "CN" : Product.Length >= 2 ? Product[..2].ToUpperInvariant() : "A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("fonts:Inter#Inter", FontStyle.Normal, FontWeight.SemiBold), side * .31, Brush.Parse("#60718D"));
            context.DrawText(text, new Point((side - text.Width) / 2, (side - text.Height) / 2));
        }
    }
}
