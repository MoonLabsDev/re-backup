using System.Windows;
using System.Windows.Controls;

namespace ReBackup.Shared.Wpf.Controls;

/// <summary>
/// Lays its children out in up to <see cref="Columns"/> equal columns, row by row; each row is as tall as its tallest
/// child and the children of a row are stretched to that height (like a CSS grid). Unlike UniformGrid, one tall tile
/// does not make every row tall. Fewer columns are used while a column would be narrower than
/// <see cref="MinColumnWidth"/>.
/// </summary>
public sealed class TileGrid : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(TileGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure), value => (int)value >= 1);

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(TileGrid),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private double[] _rowHeights = [];
    private int _columns = 1;

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    private int ColumnsFor(double width) =>
        double.IsInfinity(width) || MinColumnWidth <= 0
            ? Columns
            : Math.Clamp((int)(width / MinColumnWidth), 1, Columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = _columns = ColumnsFor(availableSize.Width);
        var columnWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / columns;
        var children = InternalChildren;
        var rows = (children.Count + columns - 1) / columns;
        _rowHeights = new double[rows];
        double widest = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            _rowHeights[i / columns] = Math.Max(_rowHeights[i / columns], child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }
        var width = double.IsInfinity(columnWidth) ? widest * columns : availableSize.Width;
        return new Size(width, _rowHeights.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = _columns;
        var columnWidth = finalSize.Width / columns;
        var children = InternalChildren;
        double top = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var row = i / columns;
            if (row >= _rowHeights.Length)
                break;
            if (i > 0 && i % columns == 0)
                top += _rowHeights[row - 1];
            children[i].Arrange(new Rect((i % columns) * columnWidth, top, columnWidth, _rowHeights[row]));
        }
        return finalSize;
    }
}
