using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EgressController.Desktop.ViewModels;
namespace EgressController.Desktop.Views;

public partial class SetupOverlay : UserControl
{
    private ShellViewModel? _model;
    private bool _restoreHomeCheckFocus;
    public SetupOverlay()
    {
        InitializeComponent(); ApplicationArtworkRequests.Attach(this);
        var checkButton = this.FindControl<Button>("HomeConnectivityButton")!;
        checkButton.GotFocus += (_, _) => _restoreHomeCheckFocus = true;
        checkButton.LostFocus += (_, _) =>
        {
            if (_model?.CanEdit != false && _model?.CheckHomeCommand.CanExecute(null) != false) _restoreHomeCheckFocus = false;
        };
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
        if (e.PropertyName == nameof(ShellViewModel.CanEdit))
        {
            var button = this.FindControl<Button>("HomeConnectivityButton");
            if (_model?.CanEdit == true && _restoreHomeCheckFocus)
            {
                _restoreHomeCheckFocus = false;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_model?.CanEdit == true && _model.SetupInterfaces && IsEffectivelyVisible) button?.Focus();
                }, DispatcherPriority.Loaded);
            }
            return;
        }
        if (e.PropertyName == nameof(ShellViewModel.HomeCheckFailed) && _model?.HomeCheckFailed == true)
        {
            // One explicit check result reveals its advice; no focus theft or periodic scroll updates.
            Dispatcher.UIThread.Post(() =>
            {
                if (_model?.HomeCheckFailed == true && _model.SetupInterfaces && IsEffectivelyVisible)
                    this.FindControl<StackPanel>("HomeCheckFeedback")?.BringIntoView();
            }, DispatcherPriority.Loaded);
            return;
        }
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
