using Avalonia.Controls;

namespace EgressController.GuardTray;

internal static class TrayIcons
{
    // Native ICO artwork: blue shield, red status dot, or gray manually-stopped shield.
    public static WindowIcon Create(bool red, bool disabled)
    {
        const int size = 32, imageBytes = 40 + size * size * 4 + size * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)size); writer.Write((byte)size); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(imageBytes); writer.Write(22);
        writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(size * size * 4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (int y = size - 1; y >= 0; y--)
            for (int x = 0; x < size; x++)
            {
                bool shield = y >= 3 && y < 29 && Math.Abs(x - 15) < (y < 18 ? 13 : 29 - y);
                bool dot = red && (x - 25) * (x - 25) + (y - 25) * (y - 25) <= 42;
                bool mark = shield && ((x is 12 or 13 && y is >= 11 and <= 20) || (y is 11 or 15 or 20 && x is >= 13 and <= 21));
                uint color = dot ? 0xFFEF4444u : mark ? 0xFFFFFFFFu : shield ? disabled ? 0xFF6B7280u : 0xFF2563EBu : 0;
                writer.Write(color);
            }
        writer.Write(new byte[size * 4]);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
