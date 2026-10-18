using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;

namespace EgressController.App.ViewModels;

public sealed class RuleEntryViewModel
{
    private readonly IAppController _controller;
    public RuleEntryViewModel(IAppController controller, string name, string path, Action changed)
    {
        _controller = controller;
        Name = name;
        Path = path;
        Routing = new RouteSelectionViewModel(controller.Profile,
            controller.Profile.RuleSets.FirstOrDefault(route => route.Name == Name)?.Target,
            (enabled, route) => controller.SetRuleSetAsync(Name, enabled, route), changed,
            error => controller.ReportFailure("规则分流", "rules.route", error.Message, error), () => controller.Adapters);
    }

    public string Name { get; }
    public string Path { get; }
    public RouteSelectionViewModel Routing { get; }
    public void RefreshRoute() => Routing.Refresh(_controller.Profile,
        _controller.Profile.RuleSets.FirstOrDefault(route => route.Name == Name)?.Target);
}
