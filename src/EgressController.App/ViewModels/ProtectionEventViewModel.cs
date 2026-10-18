using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;

namespace EgressController.App.ViewModels;

public sealed class ProtectionEventViewModel(EgressController.Core.Protection.ProtectionEvent entry)
{
    public string Summary => $"{entry.At.ToLocalTime():HH:mm:ss} · {entry.Application} · PID {entry.Pid} · {(entry.Succeeded ? "已终止" : "终止失败")}";
    public string Path => entry.Path;
    public string Detail => entry.Reason + (entry.Error is null ? "" : " · " + entry.Error);
    public bool Failed => !entry.Succeeded;
}
