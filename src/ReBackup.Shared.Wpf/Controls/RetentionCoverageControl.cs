using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;
using ReBackup.Shared.Wpf.Theme;

namespace ReBackup.Shared.Wpf.Controls;

/// <summary>How <see cref="RetentionCoverageControl"/> fills a cell that holds a version without warnings.</summary>
public enum CoveragePresentColoring
{
    /// <summary>The accent colour (warnings in the warning colour).</summary>
    Status,
    /// <summary>The colour of the lane's period, as its label (warnings still in the warning colour).</summary>
    Period,
}

/// <summary>
/// Where the selection of a <see cref="RetentionCoverageControl"/> is drawn: <paramref name="Rings"/> (the pen's centre
/// line) around the drawn cells of the selected version, <paramref name="HiddenLanes"/> whose "+N" label is ringed
/// because that cell is not drawn, and the <paramref name="Bands"/> behind the slot that holds the version's date, in
/// every lane.
/// </summary>
public sealed record CoverageSelectionLayout(IReadOnlyList<Rect> Rings, IReadOnlyList<int> HiddenLanes, IReadOnlyList<Rect> Bands)
{
    public static readonly CoverageSelectionLayout None = new([], [], []);
}

/// <summary>
/// Draws, per retention rule, the slots the rule wants to keep: filled when a version exists, red when it is missing.
/// Each lane has its own scale (equal-width cells, newest on the right edge).
/// </summary>
public sealed class RetentionCoverageControl : FrameworkElement
{
    private const double LabelWidth = 170;
    private const double LaneHeight = 26;
    private const double RightPadding = 8;
    private const double MinCell = 2;
    private const double GapFrom = 6;
    /// <summary>Room above and below a cell, together; the selection ring fits in it.</summary>
    private const double CellInset = 8;
    /// <summary>The selection ring: its thickness and the gap between it and the cell.</summary>
    private const double RingThickness = 2;
    private const double RingGap = 2;

    private static readonly Color[] LaneFallbacks =
    [
        Color.FromRgb(0x5F, 0xD3, 0xC4), Color.FromRgb(0x7F, 0xB2, 0xFF),
        Color.FromRgb(0xC9, 0xA2, 0xFF), Color.FromRgb(0xF2, 0xB8, 0x4B),
    ];
    private static readonly string[] LaneKeys =
        ["Color.Chart.TealLight", "Color.Chart.BlueLight", "Color.Chart.VioletLight", "Color.Chart.AmberLight"];
    private static readonly Typeface TextFace = new("Segoe UI");

    private sealed class Paint
    {
        public required Brush[] Lanes { get; init; }
        public required Brush Label { get; init; }
        public required Brush Present { get; init; }
        public required Brush Band { get; init; }
        public required Brush Warning { get; init; }
        public required Brush Missing { get; init; }
        public required Brush BeforeFirst { get; init; }
        public required Pen Pending { get; init; }
        public required Pen Selected { get; init; }
    }

    private Paint? _paint;

    public static readonly DependencyProperty LanesProperty = DependencyProperty.Register(
        nameof(Lanes), typeof(IReadOnlyList<CoverageLane>), typeof(RetentionCoverageControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LaneLabelsProperty = DependencyProperty.Register(
        nameof(LaneLabels), typeof(IReadOnlyList<string>), typeof(RetentionCoverageControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(
        nameof(SelectCommand), typeof(ICommand), typeof(RetentionCoverageControl), new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedIdProperty = DependencyProperty.Register(
        nameof(SelectedId), typeof(string), typeof(RetentionCoverageControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PresentColoringProperty = DependencyProperty.Register(
        nameof(PresentColoring), typeof(CoveragePresentColoring), typeof(RetentionCoverageControl),
        new FrameworkPropertyMetadata(CoveragePresentColoring.Status, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SlotTextProperty = DependencyProperty.Register(
        nameof(SlotText), typeof(Func<CoverageSlot, string>), typeof(RetentionCoverageControl), new PropertyMetadata(null));

    public RetentionCoverageControl()
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

    public IReadOnlyList<CoverageLane>? Lanes
    {
        get => (IReadOnlyList<CoverageLane>?)GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    /// <summary>The localized label of each lane (same order as <see cref="Lanes"/>).</summary>
    public IReadOnlyList<string>? LaneLabels
    {
        get => (IReadOnlyList<string>?)GetValue(LaneLabelsProperty);
        set => SetValue(LaneLabelsProperty, value);
    }

    /// <summary>Executed with the version's id when a cell that holds a version is clicked.</summary>
    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    /// <summary>The id of the version to highlight with a ring.</summary>
    public string? SelectedId
    {
        get => (string?)GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>How cells with a version (without warnings) are filled: the accent colour, or the lane's period colour.</summary>
    public CoveragePresentColoring PresentColoring
    {
        get => (CoveragePresentColoring)GetValue(PresentColoringProperty);
        set => SetValue(PresentColoringProperty, value);
    }

    /// <summary>The tooltip text of a cell.</summary>
    public Func<CoverageSlot, string>? SlotText
    {
        get => (Func<CoverageSlot, string>?)GetValue(SlotTextProperty);
        set => SetValue(SlotTextProperty, value);
    }

    /// <summary>Cell pitch (width plus gap) and the number of cells drawn, for a lane with <paramref name="slotCount"/> slots.</summary>
    private static (double Pitch, int Visible, double Right) Geometry(double width, int slotCount)
    {
        var plotWidth = width - LabelWidth - RightPadding;
        if (plotWidth < MinCell || slotCount <= 0)
            return (0, 0, 0);
        var pitch = Math.Max(MinCell, plotWidth / slotCount);
        var visible = (int)Math.Min(slotCount, Math.Floor(plotWidth / pitch + 1e-9));
        return (pitch, visible, LabelWidth + plotWidth);
    }

    /// <summary>How many of a lane's newest slots fit; the rest is counted in the "+N" label.</summary>
    public static int VisibleSlots(Size size, int slotCount) => Geometry(size.Width, slotCount).Visible;

    /// <summary>The rectangle of drawn cell <paramref name="index"/> (0 = oldest drawn) of lane <paramref name="lane"/>.</summary>
    private static Rect Cell(int lane, int index, double pitch, double left)
    {
        var gap = pitch >= GapFrom ? 1 : 0;
        return new Rect(left + index * pitch, lane * LaneHeight + CellInset / 2, pitch - gap, LaneHeight - CellInset);
    }

    /// <summary>
    /// Where the selection of <paramref name="selectedId"/> is drawn: a ring around each drawn cell that holds the
    /// version, the lanes whose "+N" label is ringed instead, and in every lane a full-height band behind the drawn slot
    /// that contains the version's date. <see cref="CoverageSelectionLayout.None"/> when no slot holds the version.
    /// </summary>
    public static CoverageSelectionLayout SelectionLayout(Size size, IReadOnlyList<CoverageLane> lanes, string? selectedId)
    {
        if (selectedId is null)
            return CoverageSelectionLayout.None;
        var version = lanes.SelectMany(l => l.Slots).Select(s => s.Version).FirstOrDefault(v => v?.Id == selectedId);
        if (version is null)
            return CoverageSelectionLayout.None;

        var date = DateOnly.FromDateTime(version.LocalTime);
        var outset = RingGap + RingThickness / 2;
        var rings = new List<Rect>();
        var hiddenLanes = new List<int>();
        var bands = new List<Rect>();
        for (var i = 0; i < lanes.Count; i++)
        {
            var slots = lanes[i].Slots;
            var (pitch, visible, right) = Geometry(size.Width, slots.Count);
            var hidden = slots.Count - visible;
            var left = right - visible * pitch;
            for (var s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                var drawn = visible > 0 && s >= hidden;
                if (slot.Version?.Id == selectedId)
                {
                    if (drawn)
                        rings.Add(Rect.Inflate(Cell(i, s - hidden, pitch, left), outset, outset));
                    else if (visible > 0 && !hiddenLanes.Contains(i))
                        hiddenLanes.Add(i);
                }
                if (drawn && slot.Start <= date && date < slot.End)
                {
                    var cell = Cell(i, s - hidden, pitch, left);
                    bands.Add(new Rect(cell.X, i * LaneHeight, cell.Width, LaneHeight));
                }
            }
        }
        return new CoverageSelectionLayout(rings, hiddenLanes, bands);
    }

    /// <summary>The fill of a Present cell: <paramref name="status"/>, or the period's colour from <paramref name="periods"/>.</summary>
    public static Brush PresentBrush(RetentionPeriod period, CoveragePresentColoring coloring, Brush status, IReadOnlyList<Brush> periods) =>
        coloring == CoveragePresentColoring.Period && (int)period >= 0 && (int)period < periods.Count ? periods[(int)period] : status;

    /// <summary>The fill of a cell in <paramref name="state"/> of a lane of <paramref name="period"/>; null for an outlined (pending) cell.</summary>
    private static Brush? Fill(SlotState state, RetentionPeriod period, CoveragePresentColoring coloring, Paint paint) => state switch
    {
        SlotState.Present => PresentBrush(period, coloring, paint.Present, paint.Lanes),
        SlotState.PresentWithWarnings => paint.Warning,
        SlotState.Missing => paint.Missing,
        SlotState.BeforeFirst => paint.BeforeFirst,
        _ => null,
    };

    /// <summary>The fill the control draws for a cell in <paramref name="state"/> of a lane of <paramref name="period"/> (null: outlined).</summary>
    public Brush? CellFill(SlotState state, RetentionPeriod period) =>
        Fill(state, period, PresentColoring, _paint ??= LoadPaint());

    /// <summary>The lane index and slot under <paramref name="p"/>, or null.</summary>
    public static int? HitTest(Size size, IReadOnlyList<CoverageLane> lanes, Point p, out CoverageSlot? slot)
    {
        slot = null;
        if (p.Y < 0 || p.X < 0)
            return null;
        var lane = (int)(p.Y / LaneHeight);
        if (lane >= lanes.Count)
            return null;
        var slots = lanes[lane].Slots;
        var (pitch, visible, right) = Geometry(size.Width, slots.Count);
        if (visible == 0)
            return null;
        var left = right - visible * pitch;
        if (p.X < left || p.X >= right)
            return null;
        var index = Math.Min(visible - 1, (int)((p.X - left) / pitch));
        slot = slots[slots.Count - visible + index];
        return lane;
    }

    /// <summary>Runs <see cref="SelectCommand"/> when a cell with a version is at <paramref name="point"/>.</summary>
    public bool Activate(Point point)
    {
        var lanes = Lanes;
        if (lanes is null || HitTest(new Size(ActualWidth, ActualHeight), lanes, point, out var slot) is null)
            return false;
        var id = slot?.Version?.Id;
        var command = SelectCommand;
        if (id is null || slot!.State is not (SlotState.Present or SlotState.PresentWithWarnings)
            || command is null || !command.CanExecute(id))
            return false;
        command.Execute(id);
        return true;
    }

    /// <summary>Sets the tooltip to the text of the cell at <paramref name="point"/> (none outside the cells).</summary>
    public void UpdateToolTip(Point point)
    {
        CoverageSlot? slot = null;
        var lanes = Lanes;
        var text = lanes is not null && SlotText is { } slotText
            && HitTest(new Size(ActualWidth, ActualHeight), lanes, point, out slot) is not null && slot is not null
                ? slotText(slot)
                : null;
        if (!Equals(ToolTip, text))
            ToolTip = text;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateToolTip(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Activate(e.GetPosition(this)))
            e.Handled = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = Lanes?.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, lanes * LaneHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var lanes = Lanes;
        if (lanes is null || lanes.Count == 0)
            return;

        var paint = _paint ??= LoadPaint();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labels = LaneLabels;

        // Transparent backdrop so the whole control is hit-testable (tooltip, click).
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, lanes.Count * LaneHeight));

        // The selected version's column behind the cells; its rings over them, at the end.
        var selection = SelectionLayout(new Size(ActualWidth, ActualHeight), lanes, SelectedId);
        foreach (var band in selection.Bands)
            drawingContext.DrawRectangle(paint.Band, null, band);

        var coloring = PresentColoring;
        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var top = i * LaneHeight;
            var (pitch, visible, right) = Geometry(ActualWidth, lane.Slots.Count);
            var hidden = lane.Slots.Count - visible;

            var labelText = labels is not null && i < labels.Count ? labels[i] : lane.Rule.Period.ToString();
            var label = Text(labelText, 12, paint.Lanes[(int)lane.Rule.Period], pixelsPerDip);
            label.MaxTextWidth = LabelWidth - (hidden > 0 ? 44 : 8);
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(label, new Point(0, top + LaneHeight / 2 - label.Height / 2));

            if (visible == 0)
                continue;
            if (hidden > 0)
            {
                var more = Text("+" + hidden.ToString(Loc.Culture), 10, paint.Label, pixelsPerDip);
                var origin = new Point(LabelWidth - 4 - more.Width, top + LaneHeight / 2 - more.Height / 2);
                drawingContext.DrawText(more, origin);
                if (selection.HiddenLanes.Contains(i))
                {
                    // The selected version's cell is not drawn: the "+N" that counts it is ringed.
                    var outset = RingGap + RingThickness / 2;
                    var box = new Rect(origin.X, top + CellInset / 2, more.Width, LaneHeight - CellInset);
                    drawingContext.DrawRectangle(null, paint.Selected, Rect.Inflate(box, outset, outset));
                }
            }

            var left = right - visible * pitch;
            for (var s = 0; s < visible; s++)
            {
                var slot = lane.Slots[hidden + s];
                var cell = Cell(i, s, pitch, left);
                if (Fill(slot.State, lane.Rule.Period, coloring, paint) is { } fill)
                    drawingContext.DrawRectangle(fill, null, cell);
                else if (slot.State == SlotState.Pending)
                    drawingContext.DrawRectangle(null, paint.Pending,
                        cell.Width > 1.5 ? new Rect(cell.X + 0.5, cell.Y + 0.5, cell.Width - 1, cell.Height - 1) : cell);
            }
        }

        foreach (var ring in selection.Rings)
            drawingContext.DrawRectangle(null, paint.Selected, ring);
    }

    private static FormattedText Text(string text, double size, Brush brush, double pixelsPerDip) =>
        new(text, Loc.Culture, FlowDirection.LeftToRight, TextFace, size, brush, pixelsPerDip);

    private void OnLanguageChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _paint = null;
        InvalidateVisual();
    }

    private Paint LoadPaint()
    {
        Color Resolve(string key, Color fallback) => ThemeResources.Color(this, key, fallback);
        var muted = Resolve("Color.TextMuted", Color.FromRgb(0x9A, 0xA4, 0xB2));
        var faint = Resolve("Color.TextFaint", Color.FromRgb(0x6B, 0x75, 0x84));
        var text = Resolve("Color.Text", Color.FromRgb(0xE6, 0xEA, 0xF0));
        return new Paint
        {
            Lanes = LaneKeys.Select((key, i) => (Brush)ThemeResources.Brush(Resolve(key, LaneFallbacks[i]))).ToArray(),
            Label = ThemeResources.Brush(muted),
            Present = ThemeResources.Brush(Resolve("Color.Accent", Color.FromRgb(0x2B, 0xB3, 0xA3))),
            Warning = ThemeResources.Brush(Resolve("Color.Warning", Color.FromRgb(0xE0, 0xA8, 0x00))),
            Missing = ThemeResources.Brush(Resolve("Color.Danger", Color.FromRgb(0xF2, 0x54, 0x5B))),
            BeforeFirst = ThemeResources.Brush(Color.FromArgb(0x59, faint.R, faint.G, faint.B)),
            Pending = ThemeResources.Pen(faint, 1),
            Band = ThemeResources.Brush(Color.FromArgb(0x1F, text.R, text.G, text.B)),
            Selected = ThemeResources.Pen(text, RingThickness),
        };
    }
}
