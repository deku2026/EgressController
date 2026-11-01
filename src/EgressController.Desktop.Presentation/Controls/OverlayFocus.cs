using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace EgressController.Desktop.Presentation.Controls;

/// <summary>Presentation-only focus handoff. Deferred data loads may enable the target after opening.</summary>
public static class OverlayFocus
{
    public static void Attach(Control overlay, Control target)
    {
        Control? previous = null;
        bool pending = false;
        void TryFocus()
        {
            if (pending && overlay.IsEffectivelyVisible && target.IsEffectivelyEnabled && target.Focus(NavigationMethod.Tab))
                pending = false;
        }
        overlay.PropertyChanged += (_, change) =>
        {
            if (change.Property != Control.IsVisibleProperty) return;
            if (overlay.IsVisible)
            {
                previous = TopLevel.GetTopLevel(overlay)?.FocusManager?.GetFocusedElement() as Control;
                pending = true;
                Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Loaded);
            }
            else
            {
                pending = false;
                Dispatcher.UIThread.Post(() => { if (previous?.IsEffectivelyVisible == true && previous.IsEffectivelyEnabled) previous.Focus(); });
            }
        };
        target.PropertyChanged += (_, change) =>
        {
            if (change.Property == InputElement.IsEffectivelyEnabledProperty)
                Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Loaded);
        };
        overlay.AttachedToVisualTree += (_, _) => { if (overlay.IsVisible) { pending = true; Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Loaded); } };
    }
}
