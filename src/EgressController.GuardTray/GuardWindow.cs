using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EgressController.Guardian;
using EgressController.Presentation;

namespace EgressController.GuardTray;

public sealed class GuardWindow : Window
{
    public bool AllowExit { get; set; }
    public bool TrayAvailable { get; set; } = true;
    public event Action<string>? OpenDirectoryRequested;
    public event Action? OpenMainRequested;
    public event Action? ExitRequested;
    public GuardWindow(GuardManagementViewModel vm, bool preview = false)
    {
        Title = "EgressController · 独立守护";
        Width = 680; Height = 780; MinWidth = 600; MinHeight = 520;
        var panel = new GuardPanel { DataContext = vm };
        panel.OpenDirectoryRequested += path => OpenDirectoryRequested?.Invoke(path);
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 18 };
        var heading = new StackPanel { Spacing = 7 };
        heading.Children.Add(new TextBlock { Text = "Egress Guard", FontSize = 22, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = preview ? "独立守护 · 模拟状态" : "主程序与 TUN 的独立守护", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#667184")) });
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        toolbar.Children.Add(heading);
        var open = new Button { Content = "打开主程序", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        open.Classes.Add("primary"); open.Click += (_, _) => OpenMainRequested?.Invoke();
        var exit = new Button { Content = "退出托盘", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        exit.Classes.Add("quiet"); ToolTip.SetTip(exit, "独立守护服务继续运行");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        Grid.SetColumn(exit, 1); toolbar.Children.Add(exit);
        Grid.SetColumn(open, 2); toolbar.Children.Add(open);
        layout.Children.Add(toolbar);
        Grid.SetRow(panel, 1); layout.Children.Add(panel);
        Content = layout;
        Closing += (_, e) =>
        {
            if (!AllowExit && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            { e.Cancel = true; if (TrayAvailable) Hide(); else WindowState = WindowState.Minimized; }
        };
    }
}
