using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EgressController.Presentation;

/// <summary>Choose a backing surface from the artwork, once per cached image. Never recolor a logo.</summary>
public static class IconContrast
{
    private sealed record Result(bool Dark);
    private static readonly ConditionalWeakTable<IImage, Result> Results = new();
    public static readonly IBrush LightSurface = Brush.Parse("#FFFFFF");
    public static readonly IBrush DarkSurface = Brush.Parse("#344054");
    public static readonly IPen LightEdge = new Pen(Brush.Parse("#E2E7EF"), .8);
    public static readonly IPen DarkEdge = new Pen(Brush.Parse("#47546A"), .8);
    public static bool NeedsDarkSurface(IImage? image) => image is not null && Results.GetValue(image, Analyze).Dark;

    private static Result Analyze(IImage image)
    {
        if (image is not Bitmap bitmap) return new(false);
        try
        {
            using var small = bitmap.PixelSize.Width > 64 || bitmap.PixelSize.Height > 64
                ? bitmap.CreateScaledBitmap(new PixelSize(24, 24), BitmapInterpolationMode.LowQuality) : null;
            var source = small ?? bitmap;
            var size = source.PixelSize;
            using var copy = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var pixels = copy.Lock(); source.CopyPixels(pixels);
            byte[] bytes = new byte[pixels.RowBytes * size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
            return new(IsLightArtwork(bytes, pixels.RowBytes, size.Width, size.Height));
        }
        catch { return new(false); } // Artwork analysis never affects app operations.
    }

    public static bool IsLightArtwork(ReadOnlySpan<byte> bgra, int stride, int width, int height)
    {
        double weight = 0, light = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * stride + x * 4; double alpha = bgra[i + 3] / 255d;
            if (alpha < .15) continue;
            double luminance = .0722 * bgra[i] + .7152 * bgra[i + 1] + .2126 * bgra[i + 2];
            weight += alpha;
            if (luminance > 190) light += alpha;
        }
        return weight > 0 && light / weight >= .7;
    }
}
