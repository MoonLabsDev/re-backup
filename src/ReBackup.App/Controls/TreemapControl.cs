using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;

namespace ReBackup.App.Controls;

/// <summary>Draws an evaluated source tree as a squarified treemap. Ignored entries are grey.</summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double MinTile = 3;    // smaller rectangles are not drawn
    private const double MinSplit = 8;   // folders smaller than this are drawn as one tile

    private static readonly Brush IgnoredBrush = Frozen(0xC8, 0xC8, 0xC8);
    private static readonly Brush FolderBrush = Frozen(0x5A, 0x6B, 0x7D);
    private static readonly Brush[] Palette =
    [
        Frozen(0x4E, 0x79, 0xA7), Frozen(0xF2, 0x8E, 0x2B), Frozen(0x59, 0xA1, 0x4F), Frozen(0xE1, 0x57, 0x59),
        Frozen(0x76, 0xB7, 0xB2), Frozen(0xED, 0xC9, 0x48), Frozen(0xB0, 0x7A, 0xA1), Frozen(0x9C, 0x75, 0x5F),
    ];
    private static readonly Pen TilePen = FrozenPen(Colors.White, 0.5);
    private static readonly Pen SelectionPen = FrozenPen(Colors.Black, 2);

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(EvaluatedNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(EvaluatedNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private readonly List<(EvaluatedNode Node, Rect Rect)> _leafTiles = [];
    private readonly Dictionary<EvaluatedNode, Rect> _rects = [];

    public EvaluatedNode? Root
    {
        get => (EvaluatedNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public EvaluatedNode? Selected
    {
        get => (EvaluatedNode?)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        _leafTiles.Clear();
        _rects.Clear();

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Brushes.Transparent, null, bounds);   // keeps the whole area hit-testable
        if (Root is not { TotalSize: > 0 } root || bounds.Width < MinTile || bounds.Height < MinTile)
            return;

        Draw(drawingContext, root, bounds);

        if (Selected is { } selected && _rects.TryGetValue(selected, out var rect))
            drawingContext.DrawRectangle(null, SelectionPen, rect);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (TileAt(e.GetPosition(this)) is { } node)
            SetCurrentValue(SelectedProperty, node);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = TileAt(e.GetPosition(this));
        var tip = node is null
            ? null
            : $"{node.Node.RelativePath}\n{ByteSize.Format(node.TotalSize)} — {node.Status}";
        if (!Equals(ToolTip, tip))
            ToolTip = tip;
    }

    private void Draw(DrawingContext dc, EvaluatedNode node, Rect rect)
    {
        if (rect.Width < MinTile || rect.Height < MinTile)
            return;
        _rects[node] = rect;

        var split = node.Node.IsDirectory && node.Children.Count > 0 && node.TotalSize > 0
                    && rect.Width >= MinSplit && rect.Height >= MinSplit;
        if (!split)
        {
            dc.DrawRectangle(BrushFor(node), TilePen, rect);
            _leafTiles.Add((node, rect));
            return;
        }

        dc.DrawRectangle(FolderBrush, null, rect);   // shows through where children are too small to draw
        var tiles = TreemapLayout.Squarify(node.Children, child => (double)child.TotalSize,
            new TreemapRect(rect.X, rect.Y, rect.Width, rect.Height));
        foreach (var tile in tiles)
        {
            Draw(dc, tile.Item,
                new Rect(tile.Rect.X, tile.Rect.Y, Math.Max(0, tile.Rect.Width), Math.Max(0, tile.Rect.Height)));
        }
    }

    private EvaluatedNode? TileAt(Point point)
    {
        foreach (var (node, rect) in _leafTiles)
        {
            if (rect.Contains(point))
                return node;
        }
        return null;
    }

    private static Brush BrushFor(EvaluatedNode node)
    {
        if (node.Status == IncludeStatus.Ignored)
            return IgnoredBrush;
        if (node.Node.IsDirectory)
            return FolderBrush;

        var name = node.Node.Name;
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? "" : name[dot..];
        var hash = string.GetHashCode(extension, StringComparison.OrdinalIgnoreCase) & 0x7FFFFFFF;
        return Palette[hash % Palette.Length];
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
