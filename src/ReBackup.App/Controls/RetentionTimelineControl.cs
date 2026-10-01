using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ReBackup.App.Services;
using ReBackup.App.Theme;
using ReBackup.App.ViewModels;
using ReBackup.Core.Retention;

namespace ReBackup.App.Controls;

/// <summary>Draws the versions that survive retention on a time axis, one lane per rule.</summary>
public sealed class RetentionTimelineControl : FrameworkElement
{
    private const double LabelWidth = 170;
    private const double LaneHeight = 22;
    private const double AxisHeight = 22;
    private const double MarkerWidth = 3;
    private const double RightPadding = 8;

    /// <summary>
    /// Theme keys of the lane colours (kept versions): one per period, the hue of the period's badge on the rule tile
    /// (daily teal, weekly blue, monthly violet, yearly amber). A lane without a period uses the last entry.
    /// </summary>
    private static readonly (string Key, Color Fallback)[] LaneColors =
    [
        ("Color.Chart.TealLight", Color.FromRgb(0x5F, 0xD3, 0xC4)),
        ("Color.Chart.BlueLight", Color.FromRgb(0x7F, 0xB2, 0xFF)),
        ("Color.Chart.VioletLight", Color.FromRgb(0xC9, 0xA2, 0xFF)),
        ("Color.Chart.AmberLight", Color.FromRgb(0xF2, 0xB8, 0x4B)),
        ("Color.TextMuted", Color.FromRgb(0x9A, 0xA4, 0xB2)),
    ];
    private static readonly Typeface TextFace = new("Segoe UI");

    /// <summary>Brushes and pens resolved from the theme; resolved again after a theme switch.</summary>
    private sealed class Paint
    {
        public required Brush[] Lanes { get; init; }
        public required Brush Label { get; init; }
        public required Pen LanePen { get; init; }
        public required Pen AxisPen { get; init; }
    }

    private Paint? _paint;

    public static readonly DependencyProperty LanesProperty = DependencyProperty.Register(
        nameof(Lanes), typeof(IReadOnlyList<TimelineLane>), typeof(RetentionTimelineControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateTime), typeof(RetentionTimelineControl),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(DateTime), typeof(RetentionTimelineControl),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public RetentionTimelineControl()
    {
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            OnThemeChanged(null, EventArgs.Empty);   // the theme may have changed while unloaded
        };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    public IReadOnlyList<TimelineLane>? Lanes
    {
        get => (IReadOnlyList<TimelineLane>?)GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    /// <summary>Left end of the time axis.</summary>
    public DateTime From
    {
        get => (DateTime)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    /// <summary>Right end of the time axis.</summary>
    public DateTime To
    {
        get => (DateTime)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = Lanes?.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, lanes == 0 ? 0 : lanes * LaneHeight + AxisHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var lanes = Lanes;
        var from = From;
        var to = To;
        var plotWidth = ActualWidth - LabelWidth - RightPadding;
        if (lanes is null || lanes.Count == 0 || plotWidth < 20 || to <= from)
            return;

        var paint = _paint ??= LoadPaint();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var totalDays = (to - from).TotalDays;
        double X(DateTime time) => LabelWidth + Math.Clamp((time - from).TotalDays / totalDays, 0, 1) * plotWidth;

        for (var i = 0; i < lanes.Count; i++)
        {
            var top = i * LaneHeight;
            var middle = top + LaneHeight / 2;
            var label = Text(lanes[i].Label, 12, paint.Label, pixelsPerDip);
            label.MaxTextWidth = LabelWidth - 8;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(label, new Point(0, middle - label.Height / 2));
            drawingContext.DrawLine(paint.LanePen, new Point(LabelWidth, middle), new Point(LabelWidth + plotWidth, middle));

            var brush = paint.Lanes[LaneColorIndex(lanes[i].Period)];
            foreach (var time in lanes[i].Times)
            {
                drawingContext.DrawRectangle(brush, null,
                    new Rect(X(time) - MarkerWidth / 2, top + 4, MarkerWidth, LaneHeight - 8));
            }
        }

        var axisTop = lanes.Count * LaneHeight;
        drawingContext.DrawLine(paint.AxisPen, new Point(LabelWidth, axisTop), new Point(LabelWidth + plotWidth, axisTop));

        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        var pixelsPerMonth = plotWidth / months;
        var step = pixelsPerMonth >= 50 ? 1 : pixelsPerMonth >= 17 ? 3 : pixelsPerMonth >= 9 ? 6 : 12;
        var labelFormat = step == 12 ? "yyyy" : "MMM yy";
        for (var tick = new DateTime(from.Year, from.Month, 1).AddMonths(1); tick <= to; tick = tick.AddMonths(1))
        {
            if ((tick.Month - 1) % step != 0)
                continue;
            var x = X(tick);
            drawingContext.DrawLine(paint.AxisPen, new Point(x, axisTop), new Point(x, axisTop + 4));
            var text = Text(tick.ToString(labelFormat, CultureInfo.CurrentCulture), 10, paint.Label, pixelsPerDip);
            if (x + 2 + text.Width <= ActualWidth)
                drawingContext.DrawText(text, new Point(x + 2, axisTop + 5));
        }
    }

    private static int LaneColorIndex(RetentionPeriod? period) => period switch
    {
        RetentionPeriod.Daily => 0,
        RetentionPeriod.Weekly => 1,
        RetentionPeriod.Monthly => 2,
        RetentionPeriod.Yearly => 3,
        _ => LaneColors.Length - 1,
    };

    private static FormattedText Text(string text, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, size, brush, pixelsPerDip);

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _paint = null;
        InvalidateVisual();
    }

    private Paint LoadPaint()
    {
        var muted = ThemeResources.Color(this, "Color.TextMuted", Color.FromRgb(0x9A, 0xA4, 0xB2));
        var border = ThemeResources.Color(this, "Color.Border", Color.FromRgb(0x23, 0x2A, 0x33));
        return new Paint
        {
            Lanes = LaneColors.Select(lane => (Brush)ThemeResources.Brush(ThemeResources.Color(this, lane.Key, lane.Fallback))).ToArray(),
            Label = ThemeResources.Brush(muted),
            LanePen = ThemeResources.Pen(border, 1),
            AxisPen = ThemeResources.Pen(muted, 1),
        };
    }
}
