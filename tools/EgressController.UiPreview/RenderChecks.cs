using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Guardian;
using EgressController.Presentation;

namespace EgressController.UiPreview;

internal sealed class BindingLog : ILogSink
{
    private readonly List<string> _failures = [];
    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == "Binding";
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values)
    { if (IsEnabled(level, area)) _failures.Add(messageTemplate + " " + string.Join(" | ", values)); }
    public void Verify() { if (_failures.Count > 0) throw new InvalidOperationException("UI binding failures:\n" + string.Join('\n', _failures.Distinct())); }
}

internal static class RenderChecks
{
    internal static void Save(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        using var bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered UI frame.");
        bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    internal static void Buttons(string directory)
    {
        var normal = new Button { Content = "打开日志目录" };
        var primary = new Button { Content = "保存设置", Classes = { "primary" } };
        var danger = new Button { Content = "强制停止", Classes = { "danger" } };
        var disabled = new Button { Content = "正在保存", Classes = { "primary" }, IsEnabled = false };
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(30) };
        panel.Children.Add(new TextBlock { Text = "交互状态与对比度", FontSize = 24 });
        foreach (var button in new[] { normal, primary, danger, disabled }) panel.Children.Add(button);
        var window = new Window { Width = 460, Height = 350, Content = panel }; window.Show();
        Save(window, Path.Combine(directory, "controls-normal.png"));
        var point = primary.TranslatePoint(new Point(primary.Bounds.Width / 2, primary.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); Save(window, Path.Combine(directory, "controls-hover.png"));
        window.MouseDown(point, MouseButton.Left); Save(window, Path.Combine(directory, "controls-pressed.png"));
        window.MouseUp(point, MouseButton.Left);
        primary.Focus(NavigationMethod.Tab); window.MouseMove(new Point(0, 0));
        Save(window, Path.Combine(directory, "controls-focus.png"));
        var content = primary.GetVisualDescendants().OfType<TextBlock>().Single();
        if (content.Foreground is not ISolidColorBrush ink || ink.Color != Colors.White)
            throw new InvalidOperationException("Primary button lost its white text.");
        foreach (var button in new[] { normal, primary, danger })
        {
            var text = (ISolidColorBrush)button.GetVisualDescendants().OfType<TextBlock>().Single().Foreground!;
            var fill = (ISolidColorBrush)button.Background!;
            double first = Luminance(text.Color), second = Luminance(fill.Color);
            if ((Math.Max(first, second) + .05) / (Math.Min(first, second) + .05) < 4.5)
                throw new InvalidOperationException("Button text contrast is below 4.5:1.");
        }
        window.Close();
    }

    internal static void GuardIcons(string directory)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "守护图标 · 16 / 24 / 32 / 48 px", FontSize = 20 });
        foreach (var state in Enum.GetValues<GuardVisualState>())
        {
            byte[] bytes = GuardianIcon.CreateBytes(state);
            if (BitConverter.ToUInt16(bytes, 4) != 6) throw new InvalidOperationException("Expected six native icon sizes.");
            for (int i = 0; i < 6; i++)
            {
                int count = BitConverter.ToInt32(bytes, 6 + i * 16 + 8), offset = BitConverter.ToInt32(bytes, 6 + i * 16 + 12);
                if (count < 8 || offset + count > bytes.Length || bytes[offset] != 137 || bytes[offset + 1] != 80)
                    throw new InvalidOperationException("Invalid PNG icon entry.");
            }
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 20 };
            row.Children.Add(new TextBlock { Text = state.ToString(), Width = 100, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            foreach (string color in new[] { "#FFFFFF", "#242936" })
            {
                var group = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 18 };
                foreach (int size in new[] { 16, 24, 32, 48 }) group.Children.Add(new GuardianGlyph { State = state, Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                row.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse(color)), Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Child = group });
            }
            panel.Children.Add(row);
        }
        var window = new Window { Width = 710, Height = 530, Content = panel }; window.Show();
        Save(window, Path.Combine(directory, "guardian-icons.png")); window.Close();
    }

    internal static void TrayMenus(string directory)
    {
        int called = 0;
        foreach (bool guard in new[] { false, true })
        {
            TrayCommand[] commands = guard ? [
                new("打开主程序", null, "open", () => { called++; return Task.CompletedTask; }),
                new("守护状态与设置", "主程序在线，TUN 已接管", "guardian", () => Task.CompletedTask),
                new("启动守护", null, "protection", () => throw new Exception("Disabled command"), () => false),
                new("停止守护", "本次开机不自动拉起", "power", () => Task.CompletedTask, Danger: true),
                new("退出小托盘", "守护服务继续运行", "power", () => Task.CompletedTask)] : [
                new("打开 EgressController", null, "open", () => { called++; return Task.CompletedTask; }),
                new("退出主程序", "独立守护继续运行", "power", () => Task.CompletedTask, Danger: true)];
            var menu = new TrayMenuWindow(guard ? "Egress Guard" : "EgressController", commands);
            menu.Show(); Save(menu, Path.Combine(directory, guard ? "tray-guard.png" : "tray-main.png"));
            menu.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            menu.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Save(menu, Path.Combine(directory, guard ? "tray-guard-focused.png" : "tray-main-focused.png"));
            var buttons = menu.GetVisualDescendants().OfType<Button>().ToArray();
            if (!buttons[1].IsFocused) throw new InvalidOperationException("Tray keyboard navigation failed.");
            buttons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (menu.IsVisible) throw new InvalidOperationException("Tray action did not close menu.");
        }
        if (called != 2) throw new InvalidOperationException("Tray actions were not dispatched.");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
