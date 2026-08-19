using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using DrawingIcon = System.Drawing.Icon;

namespace EgressController.App;

/// <summary>
/// Loads package PNG assets directly and extracts Win32-associated icons using the same
/// shell-backed approach as BCUninstaller. Extracted icons are cached so a catalog refresh does
/// not repeatedly open every executable's icon resource.
/// </summary>
internal static class AppIconLoader
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EgressController", "icons");

    public static AvaloniaBitmap? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            string extension = Path.GetExtension(path);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                return new AvaloniaBitmap(path);
            }

            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
                return null;

            string cachePath = GetCachePath(path);
            if (File.Exists(cachePath))
                return new AvaloniaBitmap(cachePath);

            using DrawingIcon? icon = ExtractAssociatedIcon(path);
            if (icon is null)
                return null;
            using System.Drawing.Bitmap image = icon.ToBitmap();
            image.Save(cachePath, ImageFormat.Png);
            return new AvaloniaBitmap(cachePath);
        }
        catch
        {
            // Icons are presentation-only; a locked/protected binary must not hide the app row.
            return null;
        }
    }

    private static DrawingIcon? ExtractAssociatedIcon(string path)
    {
        try
        {
            // Use the framework implementation first for ordinary local files. This is the
            // same shell API BCUninstaller wraps and handles Windows icon-resource selection.
            DrawingIcon? associated = DrawingIcon.ExtractAssociatedIcon(path);
            if (associated is not null)
                return associated;
        }
        catch
        {
            // Fall through to the explicit shell call for UNC/edge-case paths.
        }

        try
        {
            IntPtr[] large = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            uint count = NativeMethods.ExtractIconEx(path, 0, large, small, 1);
            if (count != 0)
            {
                IntPtr iconHandle = large[0] != IntPtr.Zero ? large[0] : small[0];
                if (iconHandle != IntPtr.Zero)
                {
                    try
                    {
                        using DrawingIcon temporary = DrawingIcon.FromHandle(iconHandle);
                        return (DrawingIcon)temporary.Clone();
                    }
                    finally
                    {
                        if (large[0] != IntPtr.Zero)
                            NativeMethods.DestroyIcon(large[0]);
                        if (small[0] != IntPtr.Zero && small[0] != large[0])
                            NativeMethods.DestroyIcon(small[0]);
                    }
                }
            }

            StringBuilder iconPath = new(path, capacity: 260);
            int index = 0;
            IntPtr handle = NativeMethods.ExtractAssociatedIcon(IntPtr.Zero, iconPath, ref index);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                using DrawingIcon temporary = DrawingIcon.FromHandle(handle);
                return (DrawingIcon)temporary.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string GetCachePath(string source)
    {
        Directory.CreateDirectory(CacheDirectory);
        string stamp = File.GetLastWriteTimeUtc(source).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + stamp))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, key + ".png");
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "ExtractAssociatedIconW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern IntPtr ExtractAssociatedIcon(
            IntPtr hInst,
            StringBuilder iconPath,
            ref int index);

        [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern uint ExtractIconEx(
            string fileName,
            int iconIndex,
            [System.Runtime.InteropServices.Out] IntPtr[] largeIcons,
            [System.Runtime.InteropServices.Out] IntPtr[] smallIcons,
            uint iconCount);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }
}
