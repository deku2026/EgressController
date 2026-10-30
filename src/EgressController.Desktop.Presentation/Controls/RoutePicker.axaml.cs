using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
namespace EgressController.Desktop.Presentation.Controls;

public partial class RoutePicker : UserControl
{
    public static readonly StyledProperty<int> SelectedIndexProperty = AvaloniaProperty.Register<RoutePicker, int>(nameof(SelectedIndex), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public RoutePicker() { InitializeComponent(); UpdateSelection(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedIndexProperty) UpdateSelection();
    }
    private void UpdateSelection()
    {
        if (HomeButton is null) return;
        HomeButton.Classes.Set("active", SelectedIndex == 0); NonHomeButton.Classes.Set("active", SelectedIndex == 1); ProxyButton.Classes.Set("active", SelectedIndex == 2);
    }
    private void OnHome(object? sender, RoutedEventArgs e) => SetCurrentValue(SelectedIndexProperty, 0);
    private void OnNonHome(object? sender, RoutedEventArgs e) => SetCurrentValue(SelectedIndexProperty, 1);
    private void OnProxy(object? sender, RoutedEventArgs e) => SetCurrentValue(SelectedIndexProperty, 2);
}
