using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EgressController.Guardian;
using EgressController.Presentation;

namespace EgressController.GuardTray;

public sealed class GuardWindow : Window
{
    public bool AllowExit { get; set; }
    public GuardWindow(GuardManagementViewModel vm)
    {
        Title = "EgressController · 独立守护";
        Width = 900; Height = 800; MinWidth = 680; MinHeight = 520;
        var panel = new GuardPanel { DataContext = vm };
        panel.OpenDirectoryRequested += path =>
        {
            try
            {
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("日志目录尚未创建：" + path);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error) { vm.ReportError(error.ToString()); }
        };
        var layout = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 22 };
        var heading = new StackPanel { Spacing = 7 };
        heading.Children.Add(new TextBlock { Text = "独立守护", FontSize = 27, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = "主程序与 TUN 的守护状态", Foreground = new SolidColorBrush(Color.Parse("#667184")) });
        layout.Children.Add(heading);
        Grid.SetRow(panel, 1); layout.Children.Add(panel);
        Content = layout;
        Closing += (_, e) =>
        {
            if (!AllowExit && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            { e.Cancel = true; Hide(); }
        };
    }
}
