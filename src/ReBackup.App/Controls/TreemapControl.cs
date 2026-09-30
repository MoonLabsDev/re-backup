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
    private static readonly Pen SelectionPen = FrozenPen(Colors.Black, 1);
    private static readonly Pen SelectionHaloPen = FrozenPen(Colors.White, 3);

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(EvaluatedNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(IPreviewEntry), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HideIgnoredProperty = DependencyProperty.Register(
        nameof(HideIgnored), typeof(bool), typeof(TreemapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly record struct Tile(EvaluatedNode Node, Rect Rect, Brush Fill, bool IsLeaf);

    private readonly List<Tile> _tiles = [];
    private EvaluatedNode? _layoutRoot;
    private Size _layoutSize;
    private bool _hasLayout;
    private bool _layoutHidesIgnored;
    private EvaluatedNode? _hovered;

    public EvaluatedNode? Root
    {
        get => (EvaluatedNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public IPreviewEntry? Selected
    {
        get => (IPreviewEntry?)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    /// <summary>Leaves ignored entries out and sizes everything by what is backed up.</summary>
    public bool HideIgnored
    {
        get => (bool)GetValue(HideIgnoredProperty);
        set => SetValue(HideIgnoredProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = new Size(ActualWidth, ActualHeight);
        var bounds = new Rect(size);
        drawingContext.DrawRectangle(Brushes.Transparent, null, bounds);   // keeps the whole area hit-testable

        var root = Root;
        var hideIgnored = HideIgnored;
        if (!_hasLayout || !ReferenceEquals(root, _layoutRoot) || size != _layoutSize || hideIgnored != _layoutHidesIgnored)
        {
            _tiles.Clear();
            _layoutHidesIgnored = hideIgnored;
            if (root is not null && WeightOf(root) > 0)
                Layout(root, bounds);
            _layoutRoot = root;
            _layoutSize = size;
            _hasLayout = true;
            _hovered = null;
        }

        foreach (var tile in _tiles)
            drawingContext.DrawRectangle(tile.Fill, tile.IsLeaf ? TilePen : null, tile.Rect);

        if (Selected is { } selected)
        {
            foreach (var tile in _tiles)
            {
                if (!ReferenceEquals(tile.Node, selected))
                    continue;
                var outline = tile.Rect;
                outline.Inflate(-1, -1);
                if (outline.Width > 0 && outline.Height > 0)
                {
                    drawingContext.DrawRectangle(null, SelectionHaloPen, outline);
                    drawingContext.DrawRectangle(null, SelectionPen, outline);
                }
                break;
            }
        }
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
        if (ReferenceEquals(node, _hovered))
            return;
        _hovered = node;
        ToolTip = node is null
            ? null
            : $"{node.Node.RelativePath}\n{ByteSize.Format(node.TotalSize)}, backup {ByteSize.Format(node.IncludedSize)} — {node.Status}";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        ToolTip = null;
    }

    private void Layout(EvaluatedNode node, Rect rect)
    {
        if (!(rect.Width >= MinTile) || !(rect.Height >= MinTile))
            return;

        var weight = WeightOf(node);
        var split = node.Node.IsDirectory && node.Children.Count > 0 && weight > 0
                    && rect.Width >= MinSplit && rect.Height >= MinSplit;
        if (!split)
        {
            _tiles.Add(new Tile(node, rect, BrushFor(node), true));
            return;
        }

        // Folder background shows through where children are too small to draw.
        _tiles.Add(new Tile(node, rect, node.Status == IncludeStatus.Ignored ? IgnoredBrush : FolderBrush, false));

        // Only children that could fill at least MinTile x MinTile are laid out.
        var minSize = weight * (MinTile * MinTile) / (rect.Width * rect.Height);
        var drawable = node.Children.Where(child => WeightOf(child) >= minSize && WeightOf(child) > 0).ToList();
        if (drawable.Count == 0)
            return;

        var tiles = TreemapLayout.Squarify(drawable, child => (double)WeightOf(child),
            new TreemapRect(rect.X, rect.Y, rect.Width, rect.Height));
        foreach (var tile in tiles)
        {
            Layout(tile.Item,
                new Rect(tile.Rect.X, tile.Rect.Y, Math.Max(0, tile.Rect.Width), Math.Max(0, tile.Rect.Height)));
        }
    }

    /// <summary>Tile area: the whole size, or only the backed-up part when ignored entries are hidden (ignored ones are then 0).</summary>
    private long WeightOf(EvaluatedNode node) => _layoutHidesIgnored ? node.IncludedSize : node.TotalSize;

    private EvaluatedNode? TileAt(Point point)
    {
        foreach (var tile in _tiles)
        {
            if (tile.IsLeaf && tile.Rect.Contains(point))
                return tile.Node;
        }
        return null;
    }

    /// <summary>FNV-1a over the upper-invariant characters, so extension colours are the same on every run.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
            hash = (hash ^ char.ToUpperInvariant(ch)) * 16777619u;
        return hash;
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
        return Palette[(int)(StableHash(extension) % (uint)Palette.Length)];
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
