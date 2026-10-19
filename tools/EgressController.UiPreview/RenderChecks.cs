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

    internal static void TrafficContrast(Window window)
    {
        var labels = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("home") || text.Classes.Contains("other")).ToArray();
        if (labels.Length < 4) throw new InvalidOperationException("Traffic exit legends are missing.");
        foreach (var label in labels)
        {
            var color = ((ISolidColorBrush)label.Foreground!).Color;
            if (1.05 / (Luminance(color) + .05) < 4.5) throw new InvalidOperationException("Traffic label contrast is below 4.5:1.");
        }
    }

    internal static void MonthlyList(string directory)
    {
        var backend = new EgressController.App.Preview.PreviewController();
        var today = DateOnly.FromDateTime(DateTime.Today);
        backend.TrafficUsage = new(today.AddDays(-29), today, DateTimeOffset.UtcNow, Enumerable.Range(0, 500)
            .Select(i => new EgressController.State.Traffic.TrafficUsage($@"C:\Mock\app{i:000}.exe", today,
                EgressController.State.Traffic.TrafficExit.Home, 10, (500 - i) * 1048576L)).ToArray());
        var vm = new EgressController.App.ViewModels.MainViewModel(backend);
        var window = new EgressController.App.MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show(); window.SelectPage("traffic"); Dispatcher.UIThread.RunJobs();
        using (window.CaptureRenderedFrame()) { }
        var list = window.GetVisualDescendants().OfType<EgressController.App.Views.ApplicationTrafficList>().Single();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, 6000); Dispatcher.UIThread.RunJobs();
        using (window.CaptureRenderedFrame()) { }
        double offset = scroll.Offset.Y;
        backend.TrafficUsage = backend.TrafficUsage with { Entries = backend.TrafficUsage.Entries.Select(row => row with { Download = row.Download + 10 }).ToArray() };
        vm.Refresh(); Save(window, Path.Combine(directory, "traffic-large-list.png"));
        if (offset < 1000 || Math.Abs(scroll.Offset.Y - offset) > 1)
            throw new InvalidOperationException("Monthly refresh reset the user's scroll position.");
        int realized = list.GetVisualDescendants().OfType<UsageBar>().Count();
        if (realized == 0 || realized > 40) throw new InvalidOperationException("Monthly application rows are not virtualized: " + realized);
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

    internal static void TrayPlacement(string directory)
    {
        foreach (bool running in new[] { false, true })
        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            var backend = new EgressController.App.Preview.PreviewController(running ? "ready" : "guard-stopped");
            backend.Guard.RefreshAsync().GetAwaiter().GetResult();
            var commands = GuardTrayCommands.Create(backend.Guard, () => { }, () => { }, () => { });
            if (commands.Single(item => item.Title == "启动守护").Enabled!() == running
                || commands.Single(item => item.Title == "停止守护").Enabled!() != running)
                throw new InvalidOperationException("Companion menu does not reflect the service state.");
            var menu = new TrayMenuWindow("Egress Guard", commands);
            menu.Show(); Dispatcher.UIThread.RunJobs();
            var size = menu.Bounds.Size;
            var surface = (Control)menu.Content!; menu.Content = null; menu.Close();
            var canvas = new Canvas();
            string state = running ? "服务运行" : "服务停止";
            var caption = new TextBlock { Text = $"独立守护小托盘 · {state} · 模拟 {scale:P0} 缩放", FontSize = 19 };
            Canvas.SetLeft(caption, 28); Canvas.SetTop(caption, 25); canvas.Children.Add(caption);
            int barHeight = (int)(44 * scale), barTop = 900 - barHeight;
            var taskbar = new Border { Width = 960, Height = barHeight, Background = new SolidColorBrush(Color.Parse("#E0E5ED")) };
            Canvas.SetTop(taskbar, barTop); canvas.Children.Add(taskbar);
            int side = (int)(24 * scale), iconLeft = 860, iconTop = barTop + (barHeight - side) / 2;
            var icon = new GuardianGlyph { Width = side, Height = side, State = backend.Guard.VisualState };
            Canvas.SetLeft(icon, iconLeft); Canvas.SetTop(icon, iconTop); canvas.Children.Add(icon);
            var anchor = new EgressController.Windows.Shell.NotificationAnchor(new(iconLeft, iconTop, iconLeft + side, iconTop + side), new(0, 0, 960, barTop), scale);
            var position = TrayMenuPlacement.Place(anchor, size);
            var scaled = new Border { Width = size.Width, Height = size.Height, Child = surface,
                RenderTransform = new ScaleTransform(scale, scale), RenderTransformOrigin = RelativePoint.TopLeft };
            Canvas.SetLeft(scaled, position.X); Canvas.SetTop(scaled, position.Y); canvas.Children.Add(scaled);
            var window = new Window { Width = 960, Height = 900, Content = canvas };
            window.Show(); Save(window, Path.Combine(directory, $"guard-tray-placement-{(running ? "running" : "stopped")}-{(int)(scale * 100)}.png")); window.Close();
        }
        var model = new EgressController.App.Preview.PreviewController("guard-stopped");
        model.Guard.RefreshAsync().GetAwaiter().GetResult();
        int opened = 0, shown = 0, exits = 0;
        var actions = GuardTrayCommands.Create(model.Guard, () => opened++, () => shown++, () => exits++);
        actions.Single(item => item.Title == "打开主程序").Execute().GetAwaiter().GetResult();
        actions.Single(item => item.Title == "启动守护").Execute().GetAwaiter().GetResult();
        if (!model.Guard.CanStop) throw new InvalidOperationException("Companion start did not reach mock service.");
        actions.Single(item => item.Title == "停止守护").Execute().GetAwaiter().GetResult();
        if (!model.Guard.CanStart) throw new InvalidOperationException("Companion stop did not reach mock service.");
        actions.Single(item => item.Title == "退出小托盘").Execute().GetAwaiter().GetResult();
        if (opened != 1 || shown != 2 || exits != 1) throw new InvalidOperationException("Companion host actions were not dispatched.");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
