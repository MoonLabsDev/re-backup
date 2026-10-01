using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReBackup.App.Services;
using ReBackup.App.Theme;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;

namespace ReBackup.App.Controls;

/// <summary>
/// Draws an evaluated source tree as a squarified treemap. Each top-level entry gets a colour family of the theme's
/// chart palette (lighter near the top, darker deeper down); ignored entries are grey.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double MinTile = 3;    // smaller rectangles are not drawn
    private const double MinSplit = 8;   // folders smaller than this are drawn as one tile
    private const int ShadeSteps = 4;    // depth 1 = lightest shade, depth ShadeSteps + 1 and deeper = darkest

    /// <summary>Theme keys of the light and the dark shade of each colour family, rotated per top-level entry.</summary>
    private static readonly (string Light, string Dark, Color LightFallback, Color DarkFallback)[] Families =
    [
        ("Color.Chart.TealLight", "Color.Chart.TealDark", Color.FromRgb(0x5F, 0xD3, 0xC4), Color.FromRgb(0x1F, 0x8A, 0x7E)),
        ("Color.Chart.BlueLight", "Color.Chart.BlueDark", Color.FromRgb(0x7F, 0xB2, 0xFF), Color.FromRgb(0x4F, 0x84, 0xD6)),
        ("Color.Chart.VioletLight", "Color.Chart.VioletDark", Color.FromRgb(0xC9, 0xA2, 0xFF), Color.FromRgb(0x9B, 0x6F, 0xE0)),
        ("Color.Chart.AmberLight", "Color.Chart.AmberDark", Color.FromRgb(0xF2, 0xB8, 0x4B), Color.FromRgb(0xD9, 0x93, 0x2B)),
    ];

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

    /// <summary>Brushes and pens resolved from the theme; resolved again after a theme switch.</summary>
    private sealed class Paint
    {
        public required Brush Background { get; init; }
        public required Brush Ignored { get; init; }
        public required Brush RootFolder { get; init; }
        public required Brush[][] Shades { get; init; }        // [family][depth step]
        public required Brush[] FolderShades { get; init; }    // [family]: shows through where children are too small
        public required Pen TilePen { get; init; }
        public required Pen SelectionPen { get; init; }
    }

    private readonly List<Tile> _tiles = [];
    private EvaluatedNode? _layoutRoot;
    private Size _layoutSize;
    private bool _hasLayout;
    private bool _layoutHidesIgnored;
    private EvaluatedNode? _hovered;
    private Paint? _paint;
    private int _nextFamily;

    public TreemapControl()
    {
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            OnThemeChanged(null, EventArgs.Empty);   // the theme may have changed while unloaded
        };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

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
        var paint = _paint ??= LoadPaint();
        var size = new Size(ActualWidth, ActualHeight);
        var bounds = new Rect(size);
        drawingContext.DrawRectangle(paint.Background, null, bounds);   // also keeps the whole area hit-testable

        var root = Root;
        var hideIgnored = HideIgnored;
        if (!_hasLayout || !ReferenceEquals(root, _layoutRoot) || size != _layoutSize || hideIgnored != _layoutHidesIgnored)
        {
            _tiles.Clear();
            _layoutHidesIgnored = hideIgnored;
            _nextFamily = 0;
            if (root is not null && WeightOf(root) > 0)
                Layout(paint, root, bounds, 0, 0);
            _layoutRoot = root;
            _layoutSize = size;
            _hasLayout = true;
            _hovered = null;
        }

        foreach (var tile in _tiles)
            drawingContext.DrawRectangle(tile.Fill, tile.IsLeaf ? paint.TilePen : null, tile.Rect);

        if (Selected is { } selected)
        {
            foreach (var tile in _tiles)
            {
                if (!ReferenceEquals(tile.Node, selected))
                    continue;
                var outline = tile.Rect;
                outline.Inflate(-1, -1);
                if (outline.Width > 0 && outline.Height > 0)
                    drawingContext.DrawRectangle(null, paint.SelectionPen, outline);
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

    /// <param name="depth">0 for the root, 1 for its children, and so on.</param>
    /// <param name="family">Colour family: chosen per top-level entry (depth 1) and kept below it.</param>
    private void Layout(Paint paint, EvaluatedNode node, Rect rect, int depth, int family)
    {
        if (!(rect.Width >= MinTile) || !(rect.Height >= MinTile))
            return;

        var weight = WeightOf(node);
        var split = node.Node.IsDirectory && node.Children.Count > 0 && weight > 0
                    && rect.Width >= MinSplit && rect.Height >= MinSplit;
        if (!split)
        {
            _tiles.Add(new Tile(node, rect, BrushFor(paint, node, depth, family), true));
            return;
        }

        // Folder background shows through where children are too small to draw.
        var folderFill = node.Status == IncludeStatus.Ignored ? paint.Ignored
            : depth == 0 ? paint.RootFolder
            : paint.FolderShades[family];
        _tiles.Add(new Tile(node, rect, folderFill, false));

        // Only children that could fill at least MinTile x MinTile are laid out.
        var minSize = weight * (MinTile * MinTile) / (rect.Width * rect.Height);
        var drawable = node.Children.Where(child => WeightOf(child) >= minSize && WeightOf(child) > 0).ToList();
        if (drawable.Count == 0)
            return;

        var tiles = TreemapLayout.Squarify(drawable, child => (double)WeightOf(child),
            new TreemapRect(rect.X, rect.Y, rect.Width, rect.Height));
        foreach (var tile in tiles)
        {
            var childFamily = depth == 0 ? _nextFamily++ % Families.Length : family;
            Layout(paint, tile.Item,
                new Rect(tile.Rect.X, tile.Rect.Y, Math.Max(0, tile.Rect.Width), Math.Max(0, tile.Rect.Height)),
                depth + 1, childFamily);
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

    private static Brush BrushFor(Paint paint, EvaluatedNode node, int depth, int family)
    {
        if (node.Status == IncludeStatus.Ignored)
            return paint.Ignored;
        return paint.Shades[family][Math.Clamp(depth - 1, 0, ShadeSteps)];
    }

    /// <summary>The tiles hold brushes of the old palette: drop paint and layout and draw again.</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _paint = null;
        _hasLayout = false;
        InvalidateVisual();
    }

    private Paint LoadPaint()
    {
        var bg = ThemeResources.Color(this, "Color.Bg", Color.FromRgb(0x0F, 0x12, 0x16));
        var card = ThemeResources.Color(this, "Color.Card", Color.FromRgb(0x17, 0x1B, 0x21));
        var text = ThemeResources.Color(this, "Color.Text", Color.FromRgb(0xE6, 0xEA, 0xF0));
        var ignored = ThemeResources.Color(this, "Color.Chart.Ignored", Color.FromRgb(0x2E, 0x37, 0x43));

        var shades = new Brush[Families.Length][];
        var folderShades = new Brush[Families.Length];
        for (var family = 0; family < Families.Length; family++)
        {
            var light = ThemeResources.Color(this, Families[family].Light, Families[family].LightFallback);
            var dark = ThemeResources.Color(this, Families[family].Dark, Families[family].DarkFallback);
            shades[family] = new Brush[ShadeSteps + 1];
            for (var step = 0; step <= ShadeSteps; step++)
                shades[family][step] = ThemeResources.Brush(ThemeResources.Blend(light, dark, (double)step / ShadeSteps));
            folderShades[family] = ThemeResources.Brush(ThemeResources.Blend(dark, bg, 0.45));
        }

        return new Paint
        {
            Background = ThemeResources.Brush(bg),
            Ignored = ThemeResources.Brush(ignored),
            RootFolder = ThemeResources.Brush(card),
            Shades = shades,
            FolderShades = folderShades,
            TilePen = ThemeResources.Pen(bg, 1),
            SelectionPen = ThemeResources.Pen(text, 2),
        };
    }
}
