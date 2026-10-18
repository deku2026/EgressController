using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Protection;

namespace EgressController.Windows.Shell;

public readonly record struct ScreenBox(int Left, int Top, int Right, int Bottom);
public sealed record NotificationAnchor(ScreenBox Icon, ScreenBox WorkArea, double Scale);

/// <summary>Owns a Win32 notification icon. Positions are physical pixels from the icon's monitor.</summary>
public sealed unsafe partial class WindowsNotificationIcon : IDisposable
{
    internal static (int Data, int Identifier, int Class) StructureSizes => (sizeof(IconData), sizeof(IconIdentifier), sizeof(WindowClass));
    private const uint CallbackMessage = 0x8000 + 91;
    private const string ClassName = "EgressController.NotificationIcon";
    private static readonly Dictionary<nint, WindowsNotificationIcon> Instances = [];
    private static ushort _class;
    private static uint _taskbarCreated;
    private readonly Guid _id;
    private nint _window, _icon;
    private bool _added, _version4, _disposed;
    private byte[] _artwork;
    private string _tip;
    public event Action? OpenRequested;
    public event Action<NotificationAnchor>? MenuRequested;
    public event Action<Exception>? Failed;

    public WindowsNotificationIcon(string stableId, byte[] artwork, string tooltip)
    {
        RuntimeSafety.RequireLiveOperations();
        _id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(stableId)).AsSpan(0, 16));
        _artwork = artwork; _tip = tooltip;
        if (_class == 0)
        {
            fixed (char* name = ClassName)
            {
                var registration = new WindowClass { Size = (uint)sizeof(WindowClass), Procedure = &WindowProcedure,
                    Instance = GetModuleHandle(null), Name = name };
                _class = RegisterClassEx(ref registration);
            }
            if (_class == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        }
        _window = CreateWindowEx(0x80, ClassName, "", 0x80000000, 0, 0, 0, 0, 0, 0, GetModuleHandle(null), 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Instances[_window] = this;
        ChangeWindowMessageFilterEx(_window, _taskbarCreated, 1, 0);
        try { Publish(rebuild: true); }
        catch { Dispose(); throw; }
    }

    public void Update(byte[] artwork, string tooltip)
    {
        if (_disposed) return;
        bool changed = !ReferenceEquals(_artwork, artwork);
        if (!changed && _tip == tooltip) return;
        _artwork = artwork; _tip = tooltip;
        try { Publish(changed); } catch (Exception error) { Failed?.Invoke(error); }
    }

    private void Publish(bool rebuild)
    {
        nint replacement = 0;
        if (rebuild || _icon == 0)
        {
            int pixels = Math.Clamp((int)Math.Round(16 * Anchor().Scale), 16, 64);
            byte[] image = SelectImage(_artwork, pixels);
            fixed (byte* bytes = image) replacement = CreateIconFromResourceEx(bytes, (uint)image.Length, 1, 0x00030000, pixels, pixels, 0);
            if (replacement == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var data = Data(); data.Flags = 0x1 | 0x2 | 0x4 | 0x20 | 0x80;
        data.Callback = CallbackMessage; data.Icon = replacement != 0 ? replacement : _icon;
        Copy(_tip, data.Tip, 128);
        bool adding = !_added;
        if (ShellNotifyIcon(adding ? 0u : 1u, ref data) == 0)
        {
            if (replacement != 0) DestroyIcon(replacement);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法显示 Windows 通知区图标。");
        }
        _added = true;
        if (replacement != 0) { if (_icon != 0) DestroyIcon(_icon); _icon = replacement; }
        if (adding) { data.Version = 4; _version4 = ShellNotifyIcon(4, ref data) != 0; }
    }

    public static byte[] SelectImage(byte[] ico, int pixels)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1) throw new InvalidDataException("Invalid ICO.");
        int count = BitConverter.ToUInt16(ico, 4);
        if (count == 0 || 6 + count * 16 > ico.Length) throw new InvalidDataException("Invalid ICO directory.");
        int selected = Enumerable.Range(0, count).OrderBy(i =>
        { int width = ico[6 + i * 16]; if (width == 0) width = 256; return width >= pixels ? width - pixels : 1000 + pixels - width; }).First();
        int length = BitConverter.ToInt32(ico, 6 + selected * 16 + 8), offset = BitConverter.ToInt32(ico, 6 + selected * 16 + 12);
        if (length <= 0 || offset < 6 + count * 16 || (long)offset + length > ico.Length) throw new InvalidDataException("Invalid ICO image.");
        return ico.AsSpan(offset, length).ToArray();
    }

    private NotificationAnchor Anchor()
    {
        var identifier = new IconIdentifier { Size = (uint)sizeof(IconIdentifier), Window = _window, Id = 1, Guid = _id };
        NativeRect rect;
        if (ShellNotifyIconGetRect(ref identifier, out rect) != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        { GetCursorPos(out var cursor); rect = new() { Left = cursor.X, Right = cursor.X + 1, Top = cursor.Y, Bottom = cursor.Y + 1 }; }
        nint monitor = MonitorFromRect(ref rect, 2);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (GetMonitorInfo(monitor, ref info) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        double scale = GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? x / 96d : 1;
        return new(new(rect.Left, rect.Top, rect.Right, rect.Bottom),
            new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), scale);
    }

    private IconData Data() => new() { Size = (uint)sizeof(IconData), Window = _window, Id = 1, Guid = _id, Flags = 0x20 };
    private static void Copy(string text, char* destination, int length) => text.AsSpan(0, Math.Min(text.Length, length - 1)).CopyTo(new Span<char>(destination, length));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (Instances.TryGetValue(window, out var instance))
        {
            try
            {
                if (message == _taskbarCreated) { instance._added = false; instance.Publish(true); }
                else if (message == 0x7E) instance.Publish(true); // display arrangement/DPI changed
                else if (message == CallbackMessage)
                {
                    uint action = instance._version4 ? (uint)((long)lParam & 0xffff) : (uint)lParam;
                    if (action is 0x400 or 0x401 || !instance._version4 && action == 0x202) instance.OpenRequested?.Invoke();
                    else if (action == 0x7B || !instance._version4 && action == 0x205) instance.MenuRequested?.Invoke(instance.Anchor());
                    return 0;
                }
            }
            catch (Exception error) { try { instance.Failed?.Invoke(error); } catch { } }
        }
        return DefWindowProc(window, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_added) { var data = Data(); ShellNotifyIcon(2, ref data); }
        Instances.Remove(_window);
        if (_window != 0) DestroyWindow(_window);
        if (_icon != 0) DestroyIcon(_icon);
        _window = _icon = 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowClass
    { public uint Size, Style; public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public char* Menu; public char* Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct IconData
    { public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon; public fixed char Tip[128]; public uint State, StateMask; public fixed char Info[256]; public uint Version; public fixed char Title[64]; public uint InfoFlags; public Guid Guid; public nint BalloonIcon; }
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandle(string? name);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)] private static partial ushort RegisterClassEx(ref WindowClass value);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] private static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll")] private static partial int ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint filter);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int DestroyIcon(nint icon);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint CreateIconFromResourceEx(byte* data, uint length, int icon, uint version, int width, int height, uint flags);
    [LibraryImport("user32.dll")] private static partial int GetCursorPos(out NativePoint point);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromRect(ref NativeRect rect, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static partial int GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)] private static partial int ShellNotifyIcon(uint message, ref IconData data);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")] private static partial int ShellNotifyIconGetRect(ref IconIdentifier identifier, out NativeRect rect);
}
