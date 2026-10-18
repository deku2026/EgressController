using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;
using EgressController.State.Quota;

namespace EgressController.App.ViewModels;

public sealed class ManualDomainViewModel : ObservableObject
{
    private readonly IAppController _controller;
    private string _status = string.Empty;
    public ManualDomainViewModel(IAppController controller, string domain, Action changed)
    {
        _controller = controller;
        Domain = domain;
        Routing = new RouteSelectionViewModel(controller.Profile,
            controller.Profile.Domains.FirstOrDefault(route => route.Name == domain)?.Target,
            (_, route) => controller.AddManualDomainAsync(domain, route), () => { },
            error => controller.ReportFailure("域名分流", "domain.route", error.Message, error), () => controller.Adapters);
        RemoveCommand = new AsyncRelayCommand(async () =>
        {
            ControllerOperationResult result = await controller.RemoveManualDomainAsync(domain);
            Status = result.Succeeded ? string.Empty : result.Error ?? "移除失败。";
            if (result.Succeeded)
                changed();
        });
    }

    public string Domain { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public RouteSelectionViewModel Routing { get; }
    public void RefreshRoute() => Routing.Refresh(_controller.Profile,
        _controller.Profile.Domains.FirstOrDefault(route => route.Name == Domain)?.Target);
    public IAsyncRelayCommand RemoveCommand { get; }
}
