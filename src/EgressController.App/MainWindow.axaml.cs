using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EgressController.App.ViewModels;

namespace EgressController.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private DispatcherTimer? _timer;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _applicationExitAllowed;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as MainViewModel;
        if (_vm is not null && _timer is null)
        {
            _vm.Refresh();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => _vm.Refresh();
            _timer.Start();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ApplicationClosePolicy.ShouldHideToTray(_applicationExitAllowed, e.CloseReason))
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            Hide();
        }

        base.OnClosing(e);
    }

    internal void AllowApplicationExit()
        => _applicationExitAllowed = true;

    private async void OnUpstreamPortLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            await _vm.Overview.CommitUpstreamPortAsync();
    }

    private async void OnUpstreamPortKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (_vm is not null)
            await _vm.Overview.CommitUpstreamPortAsync();
    }

    private async void OnConnectionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.Connections.SelectedRow is not { } row)
            return;

        var details = new StackPanel { Spacing = 2 };
        AddDetail(details, "状态", row.Status);
        AddDetail(details, "进程", row.ProcessName);
        AddDetail(details, "进程路径", row.ProcessPath);
        AddDetail(details, "目标", row.Target);
        AddDetail(details, "来源端点", row.SourceEndpoint);
        AddDetail(details, "目标端点", row.DestinationEndpoint);
        AddDetail(details, "协议", row.Protocol);
        AddDetail(details, "连接类型", row.Type);
        AddDetail(details, "出口", row.Route);
        AddDetail(details, "出口链路", row.RoutePath);
        AddDetail(details, "匹配规则", row.Reason);
        AddDetail(details, "DNS 模式", row.DnsMode);
        AddDetail(details, "流量", row.Traffic);
        AddDetail(details, "实时速度", row.Speed);
        AddDetail(details, "持续时间", row.Duration);
        AddDetail(details, "开始时间", row.StartedAt);
        AddDetail(details, "结束时间", row.ClosedAt);
        AddDetail(details, "连接 ID", row.Id);
        await ShowDialogAsync("连接详情", details, 720, 700);
    }

    private async Task ShowDialogAsync(string title, Control content, double width, double height)
    {
        await _dialogGate.WaitAsync();
        try
        {
            var close = new Button
            {
                Content = "关闭",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 90,
            };
            var panel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(close, Dock.Bottom);
            panel.Children.Add(close);
            panel.Children.Add(new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Margin = new Avalonia.Thickness(0, 0, 0, 14),
            });

            var dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = Math.Min(width, 460),
                MinHeight = Math.Min(height, 240),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Border { Padding = new Avalonia.Thickness(20), Child = panel },
            };
            close.Click += (_, _) => dialog.Close();
            await dialog.ShowDialog(this);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static void AddDetail(StackPanel parent, string label, string value)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(130)),
                new ColumnDefinition(GridLength.Star),
            },
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Gray,
            Margin = new Avalonia.Thickness(0, 7, 12, 0),
        });
        var valueBox = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
        };
        Grid.SetColumn(valueBox, 1);
        row.Children.Add(valueBox);
        parent.Children.Add(row);
    }
}
