using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReBackup.App.Localization;
using ReBackup.App.Services;
using ReBackup.App.Theme;
using ReBackup.Core.Indexing;
using ReBackup.Shared.Indexing;

namespace ReBackup.App.Controls;

/// <summary>
/// Draws an evaluated source tree as a squarified treemap. Files are coloured by their extension (the same extension
/// always gets the same colour), folders show a neutral colour where their children are too small; ignored entries are grey.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double MinTile = 3;    // smaller rectangles are not drawn
    private const double MinSplit = 8;   // folders smaller than this are drawn as one tile

    /// <summary>Colours of the files, picked by a stable hash of their extension. Readable on dark and light grounds.</summary>
    private static readonly Color[] ExtensionPalette =
    [
        Color.FromRgb(0x4E, 0x79, 0xA7), Color.FromRgb(0xF2, 0x8E, 0x2B), Color.FromRgb(0x59, 0xA1, 0x4F),
        Color.FromRgb(0xE1, 0x57, 0x59), Color.FromRgb(0x76, 0xB7, 0xB2), Color.FromRgb(0xED, 0xC9, 0x48),
        Color.FromRgb(0xB0, 0x7A, 0xA1), Color.FromRgb(0x9C, 0x75, 0x5F),
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
        public required Brush[] Extensions { get; init; }      // file colours, see ExtensionPalette
        public required Brush Folder { get; init; }            // shows through where children are too small
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

    public TreemapControl()
    {
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Loc.LanguageChanged -= OnLanguageChanged;
            Loc.LanguageChanged += OnLanguageChanged;
            OnThemeChanged(null, EventArgs.Empty);   // the theme may have changed while unloaded
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            Loc.LanguageChanged -= OnLanguageChanged;
        };
    }

    /// <summary>The tool tip of a tile, in the chosen language.</summary>
    private static string ToolTipOf(EvaluatedNode node) =>
        Loc.F("ignore.treemap.toolTip", ("path", node.Node.RelativePath), ("total", Formats.Bytes(node.TotalSize)),
            ("included", Formats.Bytes(node.IncludedSize)), ("status", Loc.T("enum.includeStatus." + node.Status)));

    /// <summary>The language changed: the tool tip of the tile under the mouse is built again.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) =>
        ToolTip = _hovered is { } node ? ToolTipOf(node) : null;

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
            if (root is not null && WeightOf(root) > 0)
                Layout(paint, root, bounds, 0);
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
            : ToolTipOf(node);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        ToolTip = null;
    }

    /// <param name="depth">0 for the root, 1 for its children, and so on.</param>
    private void Layout(Paint paint, EvaluatedNode node, Rect rect, int depth)
    {
        if (!(rect.Width >= MinTile) || !(rect.Height >= MinTile))
            return;

        var weight = WeightOf(node);
        var split = node.Node.IsDirectory && node.Children.Count > 0 && weight > 0
                    && rect.Width >= MinSplit && rect.Height >= MinSplit;
        if (!split)
        {
            _tiles.Add(new Tile(node, rect, BrushFor(paint, node), true));
            return;
        }

        // Folder background shows through where children are too small to draw.
        var folderFill = node.Status == IncludeStatus.Ignored ? paint.Ignored
            : depth == 0 ? paint.RootFolder
            : paint.Folder;
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
            Layout(paint, tile.Item,
                new Rect(tile.Rect.X, tile.Rect.Y, Math.Max(0, tile.Rect.Width), Math.Max(0, tile.Rect.Height)),
                depth + 1);
        }
    }

    /// <summary>Tile area: the whole size, or only the backed-up part when ignored entries are hidden (ignored ones are then 0).</summary>
    private long WeightOf(EvaluatedNode node) => _layoutHidesIgnored ? node.IncludedSize : node.TotalSize;

    /// <summary>
    /// The deepest tile under the point. Tiles are added parent first, so searching from the end finds a file before
    /// its folders; a folder's own area (where its children are too small to draw) selects that folder.
    /// </summary>
    private EvaluatedNode? TileAt(Point point)
    {
        for (var i = _tiles.Count - 1; i >= 0; i--)
        {
            if (_tiles[i].Rect.Contains(point))
                return _tiles[i].Node;
        }
        return null;
    }

    private static Brush BrushFor(Paint paint, EvaluatedNode node)
    {
        if (node.Status == IncludeStatus.Ignored)
            return paint.Ignored;
        if (node.Node.IsDirectory)
            return paint.Folder;

        var name = node.Node.Name;
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? "" : name[dot..].ToLowerInvariant();
        return paint.Extensions[(int)(StableHash(extension) % (uint)paint.Extensions.Length)];
    }

    /// <summary>The tiles hold brushes of the old palette: drop paint and layout and draw again.</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _paint = null;
        _hasLayout = false;
        InvalidateVisual();
    }

    /// <summary>FNV-1a: stable across runs, unlike string.GetHashCode.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    private Paint LoadPaint()
    {
        var bg = ThemeResources.Color(this, "Color.Bg", Color.FromRgb(0x0F, 0x12, 0x16));
        var card = ThemeResources.Color(this, "Color.Card", Color.FromRgb(0x17, 0x1B, 0x21));
        var text = ThemeResources.Color(this, "Color.Text", Color.FromRgb(0xE6, 0xEA, 0xF0));
        var ignored = ThemeResources.Color(this, "Color.Chart.Ignored", Color.FromRgb(0x2E, 0x37, 0x43));


        var muted = ThemeResources.Color(this, "Color.TextMuted", Color.FromRgb(0x9A, 0xA4, 0xB2));
        return new Paint
        {
            Extensions = ExtensionPalette.Select(color => ThemeResources.Brush(color)).ToArray(),
            Folder = ThemeResources.Brush(ThemeResources.Blend(muted, bg, 0.55)),
            Background = ThemeResources.Brush(bg),
            Ignored = ThemeResources.Brush(ignored),
            RootFolder = ThemeResources.Brush(card),
            TilePen = ThemeResources.Pen(bg, 1),
            SelectionPen = ThemeResources.Pen(text, 2),
        };
    }
}
