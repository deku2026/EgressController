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
