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

    private async void OnConnectionRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ConnectionRowViewModel row })
            return;

        e.Handled = true;
        var details = new StackPanel { Spacing = 8 };
        AddDetail(details, "时间", row.Timestamp);
        AddDetail(details, "进程", row.ProcessName);
        AddDetail(details, "PID", row.Pid);
        AddDetail(details, "实际 EXE", row.Executable);
        AddDetail(details, "Launch Session", row.Session);
        AddDetail(details, "目标", row.Host);
        AddDetail(details, "决策", row.Decision);
        AddDetail(details, "原因", row.Reason);
        AddDetail(details, "规则集", row.RuleSet);
        AddDetail(details, "命中规则", row.RuleText);
        AddDetail(details, "出口 / 接口", row.Interface);
        AddDetail(details, "状态", row.Status);
        AddDetail(details, "字节", row.Bytes);
        AddDetail(details, "延迟", row.Latency);

        try { await ShowDialogAsync("连接详情", details, width: 720, height: 650); }
        catch
        {
            // The owner may be closing while a double-click dialog is being created.
        }
    }

    private void OnConnectionColumnDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null || ConnectionHeaderGrid.ColumnDefinitions.Count < 13)
            return;

        ConnectionColumnLayout columns = _vm.Connections.Columns;
        columns.Time = ConnectionHeaderGrid.ColumnDefinitions[0].Width;
        columns.Source = ConnectionHeaderGrid.ColumnDefinitions[2].Width;
        columns.Target = ConnectionHeaderGrid.ColumnDefinitions[4].Width;
        columns.Decision = ConnectionHeaderGrid.ColumnDefinitions[6].Width;
        columns.Reason = ConnectionHeaderGrid.ColumnDefinitions[8].Width;
        columns.Rule = ConnectionHeaderGrid.ColumnDefinitions[10].Width;
        columns.Status = ConnectionHeaderGrid.ColumnDefinitions[12].Width;
    }

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
