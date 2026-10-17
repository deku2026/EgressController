using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EgressController.Guardian;
using System.Windows.Input;

namespace EgressController.GuardTray;

public sealed class GuardWindow : Window
{
    public bool AllowExit { get; set; }
    public GuardWindow(GuardManagementViewModel vm)
    {
        Title = "EgressController · 独立守护";
        Width = 820; Height = 680; MinWidth = 620; MinHeight = 420;
        Background = new SolidColorBrush(Color.Parse("#F5F7FC"));
        var heading = new TextBlock { Text = "独立守护与服务管理", FontSize = 24, FontWeight = FontWeight.SemiBold };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16 };
        var details = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed };
        var records = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        var failure = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed };
        var automatic = new CheckBox { Content = "开机启动守护服务，登录后显示托盘" };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        void Button(string text, ICommand command) => buttons.Children.Add(new Button { Content = text, Command = command, Margin = new Thickness(0, 0, 8, 8) });
        Button("安装服务", vm.InstallCommand); Button("启动", vm.StartCommand); Button("停止", vm.StopCommand);
        Button("强制停止", vm.ForceStopCommand); Button("停止并关闭自启", vm.StopAndDisableCommand);
        Button("卸载服务", vm.UninstallCommand); Button("刷新", vm.RefreshCommand);
        Button("清空记录", vm.ClearHistoryCommand);
        bool updating = false;
        void Update()
        {
            updating = true;
            summary.Text = vm.Summary; details.Text = vm.Details; message.Text = vm.Message;
            failure.Text = vm.LastFailure;
            records.Text = vm.Records; automatic.IsChecked = vm.AutoStart;
            automatic.IsEnabled = buttons.IsEnabled = vm.CanEdit;
            updating = false;
        }
        automatic.IsCheckedChanged += (_, _) => { if (!updating) vm.AutoStart = automatic.IsChecked == true; };
        vm.PropertyChanged += (_, _) => Update();
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(heading); panel.Children.Add(summary);
        void LogDirectory(string label, string path)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) =>
            {
                try
                {
                    if (!Directory.Exists(path)) throw new DirectoryNotFoundException("日志目录尚未创建：" + path);
                    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception error) { vm.ReportError(error.ToString()); }
            };
            panel.Children.Add(button);
            panel.Children.Add(new SelectableTextBlock { Text = path, TextWrapping = TextWrapping.Wrap });
        }
        LogDirectory("打开服务日志目录", vm.LogDirectory);
        LogDirectory("打开托盘日志目录", Path.GetDirectoryName(vm.DiagnosticLogPath)!);
        panel.Children.Add(new TextBlock
        {
            Text = "仅监控主程序存活与 TUN 接管。网络保护由主程序独立执行。关闭此窗口不停止服务。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(details); panel.Children.Add(automatic); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock
        {
            Text = "手动停止后本次开机不会自动拉起。取消自启不停止当前服务。卸载后不会自动重装；主程序的网络保护不受这些操作控制。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
        });
        panel.Children.Add(message);
        panel.Children.Add(failure);
        panel.Children.Add(new TextBlock { Text = "独立守护终止记录 · 最近 24 小时，最多 200 条", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(records);
        Content = new ScrollViewer { Content = panel };
        Closing += (_, e) => { if (!AllowExit && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined) { e.Cancel = true; Hide(); } };
        Update();
    }
}
