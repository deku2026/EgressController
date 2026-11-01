using Avalonia;
using Avalonia.Controls;
namespace EgressController.Desktop.Presentation.Controls;

/// <summary>Equal-width tiles fill the available group width without a clipped last column.</summary>
public sealed class MemberTilePanel : Panel
{
    private const double Gap = 8, Minimum = 180;
    private int _columns = 1;
    private double _rowHeight;
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 720;
        _columns = Math.Max(1, (int)((width + Gap) / (Minimum + Gap)));
        double tileWidth = (width - (_columns - 1) * Gap) / _columns;
        _rowHeight = 0;
        foreach (var child in Children) { child.Measure(new(tileWidth, double.PositiveInfinity)); _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height); }
        int rows = (Children.Count + _columns - 1) / _columns;
        return new(width, rows == 0 ? 0 : rows * (_rowHeight + Gap) - Gap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = (finalSize.Width - (_columns - 1) * Gap) / _columns;
        for (int i = 0; i < Children.Count; i++) Children[i].Arrange(new Rect(i % _columns * (width + Gap), i / _columns * (_rowHeight + Gap), width, _rowHeight));
        return finalSize;
    }
}
