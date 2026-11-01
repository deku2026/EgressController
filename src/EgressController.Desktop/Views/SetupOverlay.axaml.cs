using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EgressController.Desktop.ViewModels;
namespace EgressController.Desktop.Views;

public partial class SetupOverlay : UserControl
{
    private ShellViewModel? _model;
    public SetupOverlay()
    {
        InitializeComponent(); ApplicationArtworkRequests.Attach(this);
        DataContextChanged += (_, _) => ObserveModel();
        DetachedFromVisualTree += (_, _) => { if (_model is not null) _model.PropertyChanged -= ModelChanged; _model = null; };
        AttachedToVisualTree += (_, _) => ObserveModel();
    }
    private void ObserveModel()
    {
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        _model = DataContext as ShellViewModel;
        if (_model is not null) _model.PropertyChanged += ModelChanged;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ShellViewModel.SetupStep) or nameof(ShellViewModel.OnboardingVisible)) || _model?.OnboardingVisible != true) return;
        // Reset only on explicit step/open transitions, never on row/probe/status refresh.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible) return;
            this.FindControl<TextBlock>("SetupStepTitle")?.Focus();
            this.FindControl<ScrollViewer>("SetupContentScroll")?.ScrollToHome();
        }, DispatcherPriority.Loaded);
    }
}
