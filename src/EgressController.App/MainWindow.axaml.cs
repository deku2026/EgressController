using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EgressController.App.ViewModels;

namespace EgressController.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private DispatcherTimer? _timer;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _esimWarningOpen;

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

    private async void OnImportRulesDirectoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择 meta-rules-dat 根目录或 geo\\geosite 目录",
                AllowMultiple = false,
            });
        IStorageFolder? folder = folders.FirstOrDefault();
        string? path = folder?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            _vm.Domains.ImportLocalDirectory(path);
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
        AddDetail(details, "Managed Session", row.Session);
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

    internal async Task ShowEsimDisconnectedWarningAsync(EsimConnectivityChangedEventArgs args)
    {
        if (_esimWarningOpen)
            return;

        _esimWarningOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "⚠  eSIM 已断开",
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.DarkOrange,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"检测到网卡“{args.AdapterName}”离线。\n"
                    + $"已先关闭 {args.ClosedConnections} 个活动连接，现在拒绝所有新连接，不会回落到上游或普通直连。\n"
                    + "eSIM 恢复在线后，拒绝状态会自动解除。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
            });
            content.Children.Add(new TextBlock
            {
                Text = "检测时间：" + args.DetectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Foreground = Brushes.Gray,
                FontSize = 12,
            });

            await ShowDialogAsync("eSIM 断线警告", content, width: 560, height: 300);
        }
        finally
        {
            _esimWarningOpen = false;
        }
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
