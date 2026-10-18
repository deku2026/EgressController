using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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

    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
