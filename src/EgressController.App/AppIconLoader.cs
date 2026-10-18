using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Protection;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace EgressController.App;

/// <summary>Windows resource/shell extraction at 64 px; HICON ownership never escapes this boundary.</summary>
internal static unsafe partial class AppIconLoader
{
    public static Bitmap? Load(string? path)
    {
        RuntimeSafety.RequireLiveOperations();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            string extension = Path.GetExtension(path);
            if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".ico" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return new Bitmap(path);
            if (!new[] { ".exe", ".dll", ".com" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return null;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EgressController", "icons");

            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\0" + File.GetLastWriteTimeUtc(path).Ticks + "\0shell64")));
            string cached = Path.Combine(directory, key + ".png");
            try { if (File.Exists(cached)) return new Bitmap(cached); } catch { /* A damaged cache must not hide a valid EXE icon. */ }
            nint handle = 0; uint resource = 0;
            uint count = PrivateExtractIcons(path, 0, 64, 64, &handle, &resource, 1, 0);
            if (count == 0 || count == uint.MaxValue || handle == 0)
            {
                if (handle != 0) { DestroyIcon(handle); handle = 0; }
                var info = new ShellFileInfo();
                if (SHGetFileInfo(path, 0, ref info, (uint)sizeof(ShellFileInfo), 0x100) == 0) return null;
                handle = info.Icon;
            }
            if (handle == 0) return null;
            try
            {
                using var icon = Icon.FromHandle(handle);
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
                byte[] bytes = stream.ToArray();
                try { Directory.CreateDirectory(directory); File.WriteAllBytes(cached, bytes); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                stream.Position = 0; return new Bitmap(stream);
            }
            finally { DestroyIcon(handle); }
        }
        catch { return null; } // Decoration cannot affect the protection verdict.
    }
    public static string? Describe(string path)
    {
        RuntimeSafety.RequireLiveOperations();
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            string? name = info.FileDescription?.Trim();
            return string.IsNullOrWhiteSpace(name) || name.Length > 90 || name.Contains("Operating System", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ShellFileInfo
    { public nint Icon; public int IconIndex; public uint Attributes; public fixed char DisplayName[260]; public fixed char TypeName[80]; }
    [LibraryImport("user32.dll", EntryPoint = "PrivateExtractIconsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PrivateExtractIcons(string path, int index, int width, int height, nint* icons, uint* ids, uint count, uint flags);
    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [LibraryImport("user32.dll")] private static partial int DestroyIcon(nint icon);
}
