using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Desktop.ViewModels;

namespace EgressController.Desktop.Views;

/// <summary>Presentation intent only: ask the view model for artwork of realized, visible rows.</summary>
internal static class ApplicationArtworkRequests
{
    public static void Attach(UserControl view)
    {
        void Request()
        {
            if (!view.IsEffectivelyVisible || view.DataContext is not ShellViewModel vm || vm.IsPreview) return;
            var bundles = view.GetVisualDescendants().OfType<ListBox>().Where(list => list.IsEffectivelyVisible)
                .SelectMany(list => list.GetVisualDescendants().OfType<ListBoxItem>().Where(container =>
                {
                    var point = container.TranslatePoint(default, list);
                    return point is { } at && at.Y + container.Bounds.Height >= 0 && at.Y <= list.Bounds.Height;
                }))
                .Select(container => container.DataContext switch { AppRow row => row.BundlePath, CatalogRow row => row.Model.BundlePath, _ => null })
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            _ = vm.RequestIconsAsync(bundles);
        }
        void Schedule() => Dispatcher.UIThread.Post(Request, DispatcherPriority.Loaded);
        view.AttachedToVisualTree += (_, _) => Schedule();
        view.PropertyChanged += (_, change) => { if (change.Property == Visual.IsVisibleProperty) Schedule(); };
        view.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => Schedule(), RoutingStrategies.Bubble);
        view.Loaded += (_, _) =>
        {
            foreach (var list in view.GetVisualDescendants().OfType<ListBox>()) list.ContainerPrepared += (_, _) => Schedule();
            Schedule();
        };
    }
}
