using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EgressController.Windows.Shell;

namespace EgressController.Presentation;

public sealed record TrayCommand(string Title, string? Detail, string Icon, Func<Task> Execute, Func<bool>? Enabled = null, bool Danger = false);

public static class TrayMenuPlacement
{
    public const double ShadowPadding = 12;
    public static PixelPoint Place(NotificationAnchor anchor, Size logicalSize)
    {
        double scale = double.IsFinite(anchor.Scale) && anchor.Scale > 0 ? anchor.Scale : 1;
        int width = (int)Math.Ceiling(logicalSize.Width * scale), height = (int)Math.Ceiling(logicalSize.Height * scale);
        int gap = (int)Math.Round(6 * scale);
        int shadow = (int)Math.Round(ShadowPadding * scale);
        int x = anchor.Icon.Right - width + shadow;
        int y = anchor.Icon.Top - height + shadow - gap;
        if (y + shadow < anchor.WorkArea.Top) y = anchor.Icon.Bottom - shadow + gap;
        // Constrain the visible card, not its transparent shadow padding. Clamping the
        // entire HWND pushes the card away from the clicked icon on a bottom taskbar.
        x = Math.Clamp(x, anchor.WorkArea.Left - shadow, Math.Max(anchor.WorkArea.Left - shadow, anchor.WorkArea.Right - width + shadow));
        y = Math.Clamp(y, anchor.WorkArea.Top - shadow, Math.Max(anchor.WorkArea.Top - shadow, anchor.WorkArea.Bottom - height + shadow));
        return new(x, y);
    }
}

/// <summary>A single menu surface for both notification icons; no private Avalonia APIs.</summary>
public sealed class TrayMenuWindow : Window
{
    private readonly List<Button> _buttons = [];
    public event Action<Exception>? ActionFailed;
    public TrayMenuWindow(string title, IReadOnlyList<TrayCommand> commands)
    {
        Title = title;
        WindowDecorations = WindowDecorations.None; CanResize = false;
        ShowInTaskbar = false; Topmost = true; ShowActivated = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent]; Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#697386")), Margin = new Thickness(12, 6, 12, 8) });
        foreach (var item in commands)
        {
            if (item.Danger) panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#E5E8EF")), Margin = new Thickness(10, 5) });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), ColumnSpacing = 12 };
            row.Children.Add(new PathIcon { Data = Icons.For(item.Icon), Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center });
            var labels = new StackPanel { Spacing = 3 };
            labels.Children.Add(new TextBlock { Text = item.Title, FontSize = 14, FontWeight = FontWeight.Medium });
            if (!string.IsNullOrWhiteSpace(item.Detail)) labels.Children.Add(new TextBlock { Text = item.Detail, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#697386")), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = item.Enabled?.Invoke() != false,
                Padding = new Thickness(12, 9), MinHeight = 40 };
            Avalonia.Automation.AutomationProperties.SetName(button, item.Title);
            button.Classes.Add("menu-action"); if (item.Danger) button.Classes.Add("menu-danger");
            button.Click += async (_, _) => { Close(); try { await item.Execute(); } catch (Exception error) { ActionFailed?.Invoke(error); } };
            panel.Children.Add(button); _buttons.Add(button);
        }
        Content = new Border { Margin = new Thickness(TrayMenuPlacement.ShadowPadding), Padding = new Thickness(7), Width = 280,
            CornerRadius = new CornerRadius(13), Background = Brushes.White,
            BoxShadow = BoxShadows.Parse("0 5 18 0 #300E1830"), Child = panel };
        Deactivated += (_, _) => Close();
        Opened += (_, _) => _buttons.FirstOrDefault(button => button.IsEnabled)?.Focus(NavigationMethod.Pointer);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            if (e.Key is not (Key.Up or Key.Down)) return;
            var enabled = _buttons.Where(button => button.IsEnabled).ToArray(); if (enabled.Length == 0) return;
            int selected = Array.FindIndex(enabled, button => button.IsFocused);
            enabled[(selected + (e.Key == Key.Down ? 1 : -1) + enabled.Length) % enabled.Length].Focus(NavigationMethod.Directional);
            e.Handled = true;
        };
    }
    public void ShowAt(NotificationAnchor anchor)
    {
        Measure(Size.Infinity);
        Position = TrayMenuPlacement.Place(anchor, DesiredSize);
        Show();
        Dispatcher.UIThread.Post(() => { if (IsVisible) Position = TrayMenuPlacement.Place(anchor, Bounds.Size); }, DispatcherPriority.Loaded);
    }
}

public sealed class WindowsTrayPresenter : IDisposable
{
    private readonly WindowsNotificationIcon _native;
    private readonly Func<IReadOnlyList<TrayCommand>> _commands;
    private readonly string _title;
    private TrayMenuWindow? _menu;
    private bool _disposed;
    public event Action<Exception>? Failed;
    public WindowsTrayPresenter(string id, string title, byte[] icon, string tooltip, Action open, Func<IReadOnlyList<TrayCommand>> commands)
    {
        _title = title; _commands = commands;
        _native = new(id, icon, tooltip);
        _native.OpenRequested += () => Dispatcher.UIThread.Post(() => { if (!_disposed) open(); });
        _native.MenuRequested += anchor => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            try
            {
                _menu?.Close(); _menu = new(_title, _commands());
                _menu.ActionFailed += error => Failed?.Invoke(error);
                _menu.ShowAt(anchor);
            }
            catch (Exception error) { Failed?.Invoke(error); }
        });
        _native.Failed += error => Dispatcher.UIThread.Post(() => Failed?.Invoke(error));
    }
    public void Update(byte[] icon, string tooltip) => _native.Update(icon, tooltip);
    public void Dispose() { _disposed = true; _menu?.Close(); _native.Dispose(); }
}
