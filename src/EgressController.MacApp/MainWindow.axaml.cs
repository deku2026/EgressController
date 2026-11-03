using System.ComponentModel;
using Avalonia.Controls;
using EgressController.MacApp.ViewModels;
using EgressController.SubscriptionUI;

namespace EgressController.MacApp;
public sealed partial class MainWindow : Window
{
    private MacMainViewModel? _vm;
    private readonly Dictionary<string, Control> _pages = [];
    public MainWindow()
    {
        InitializeComponent(); DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= Changed;
            _vm = DataContext as MacMainViewModel;
            if (_vm is not null) { _vm.PropertyChanged += Changed; ShowPage(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MacMainViewModel.SelectedPage)) ShowPage(); }
    private void ShowPage()
    {
        if (_vm is null) return;
        if (!_pages.TryGetValue(_vm.SelectedPage.Key, out var page))
        {
            page = _vm.SelectedPage.Key switch
            {
                "network" => new Views.NetworkPage(), "apps" => new Views.ApplicationsPage(), "rules" => new Views.RulesPage(),
                "subscriptions" => new SubscriptionPage(), "configuration" => new Views.ConfigurationPage(), "events" => new Views.EventsPage(), "settings" => new Views.SettingsPage(), _ => new Views.OverviewPage()
            };
            page.DataContext = _vm.SelectedPage.Key == "subscriptions" ? _vm.Subscriptions : _vm;
            _pages.Add(_vm.SelectedPage.Key, page);
        }
        PageHost.Content = page;
    }
}
