using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Desktop.ViewModels;

namespace EgressController.Desktop.Views;

public partial class ConnectionWorkspaceView : UserControl
{
    private ConnectionWorkspaceViewModel? _model;
    private ConnectionRow? _anchor, _focusRow;
    private int _anchorIndex;
    private double _rowHeight = 31;
    private Vector _offset;
    public ConnectionWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        DataContextChanged += (_, _) => ObserveModel();
        AttachedToVisualTree += (_, _) => ObserveModel();
        DetachedFromVisualTree += (_, _) => ReleaseModel();
    }
    private void ApplyResponsiveLayout()
    {
        // A compact operational table retains every column; the app rail becomes a filter above it.
        bool compact = Bounds.Width < 850;
        this.FindControl<Border>("ConnectionApplicationRail")!.IsVisible = !compact;
        this.FindControl<ComboBox>("CompactConnectionAppFilter")!.IsVisible = compact;
    }
    private void ObserveModel()
    {
        ReleaseModel();
        _model = DataContext as ConnectionWorkspaceViewModel;
        if (_model is not null) { _model.ReconciliationStarting += CaptureViewport; _model.ReconciliationFinished += RestoreViewport; }
    }
    private void ReleaseModel()
    {
        if (_model is not null) { _model.ReconciliationStarting -= CaptureViewport; _model.ReconciliationFinished -= RestoreViewport; }
        _model = null;
    }
    private void CaptureViewport()
    {
        _anchor = null; _focusRow = null;
        var table = this.FindControl<ListBox>("ConnectionTable")!;
        var scroll = table.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_model is null || scroll is null || _model.VisibleRows.Count == 0) return;
        _offset = scroll.Offset;
        if (_offset.Y <= 0) return; // At the top, new arrivals remain visible.
        _rowHeight = table.GetVisualDescendants().OfType<ListBoxItem>().Select(row => row.Bounds.Height).FirstOrDefault(height => height > 0, 31);
        _anchorIndex = Math.Min(_model.VisibleRows.Count - 1, (int)(_offset.Y / _rowHeight));
        _anchor = _model.VisibleRows[_anchorIndex];
        if (table.IsKeyboardFocusWithin) _focusRow = _model.SelectedConnection;
    }
    private void RestoreViewport()
    {
        var model = _model; var anchor = _anchor; var focused = _focusRow; int oldIndex = _anchorIndex; var offset = _offset; double rowHeight = _rowHeight;
        if (model is null || anchor is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(model, _model)) return;
            int index = model.VisibleRows.IndexOf(anchor); if (index < 0) return;
            var table = this.FindControl<ListBox>("ConnectionTable")!;
            var scroll = table.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null) scroll.Offset = new Vector(offset.X, Math.Max(0, offset.Y + (index - oldIndex) * rowHeight));
            if (focused is not null && table.ContainerFromItem(focused) is Control row) row.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }
}
