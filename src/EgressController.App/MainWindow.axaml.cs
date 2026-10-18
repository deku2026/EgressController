using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EgressController.App.ViewModels;

namespace EgressController.App;

public partial class MainWindow : Window
{
    public bool TrayAvailable { get; set; } = true;
    public Func<Task>? ExitRequested { get; set; }
    private MainViewModel? _vm;
    private DispatcherTimer? _timer;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _applicationExitAllowed;
    private readonly Dictionary<string, Control> _pages = new();

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
            ShowSelectedPage();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
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
        if (_vm?.IsPreview != true && ApplicationClosePolicy.ShouldHideToTray(_applicationExitAllowed, e.CloseReason))
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            if (TrayAvailable) Hide();
        }

        base.OnClosing(e);
    }

    internal void AllowApplicationExit()
        => _applicationExitAllowed = true;

    internal async void OnExitRequested(object? sender, RoutedEventArgs e)
    { if (_vm?.IsPreview == true) return; if (ExitRequested is not null) await ExitRequested(); }

    internal void OnShowDiagnostics(object? sender, RoutedEventArgs e) => SelectPage("events");
    public void SelectPage(string key)
    {
        if (_vm is null) return;
        _vm.SelectedPage = _vm.Pages.First(page => page.Key == key);
        ShowSelectedPage();
    }
    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || sender is not ListBox { SelectedItem: NavigationItem page }) return;
        _vm.SelectedPage = page;
        ShowSelectedPage();
    }
    private void ShowSelectedPage()
    {
        if (_vm is null) return;
        string key = _vm.SelectedPage.Key;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "network" => new Views.NetworkPage(), "apps" => new Views.ApplicationsPage(),
                "rules" => new Views.RulesPage(), "connections" => new Views.ConnectionsPage(),
                "traffic" => new Views.TrafficPage(), "protection" => new Views.ProtectionPage(),
                "guardian" => new Views.GuardianPage(), "events" => new Views.EventsPage(),
                _ => new Views.OverviewPage(),
            };
            page.DataContext = _vm;
            _pages[key] = page;
        }
        PageHost.Content = page;
    }

    internal void OpenLogDirectoryPath(string path)
    {
        if (_vm is null) return;
        if (_vm.IsPreview) { SelectPage("events"); return; }
        try
        {
            Directory.CreateDirectory(path);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { _vm.Controller.ReportFailure("日志目录", "logs.open", error.Message, error); }
    }

    internal void OnOpenLogDirectory(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsPreview) { SelectPage("events"); return; }
        try
        {
            Directory.CreateDirectory(_vm.Diagnostics.DirectoryPath);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                _vm.Diagnostics.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception error) { _vm.Controller.ReportFailure("日志目录", "logs.open", error.Message, error); _vm.Diagnostics.Refresh(); }
    }

    internal async void OnCopyDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsPreview) { SelectPage("events"); return; }
        try
        {
            _vm.Diagnostics.Refresh();
            var clipboard = GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板不可用。");
            await clipboard.SetTextAsync(_vm.Diagnostics.ExportText);
        }
        catch (Exception error) { _vm.Controller.ReportFailure("复制诊断", "logs.copy", error.Message, error); _vm.Diagnostics.Refresh(); }
    }

    internal void OnOpenGuardLogDirectory(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsPreview) { SelectPage("events"); return; }
        try
        {
            Directory.CreateDirectory(_vm.Guard.LogDirectory);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                _vm.Guard.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception error) { _vm.Controller.ReportFailure("守护日志目录", "guard.logs.open", error.Message, error); _vm.Diagnostics.Refresh(); }
    }

    internal async void OnConnectionDoubleTapped(object? sender, TappedEventArgs e)
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
