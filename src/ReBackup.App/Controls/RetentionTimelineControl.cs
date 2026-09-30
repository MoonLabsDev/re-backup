using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Controls;

/// <summary>Draws the versions that survive retention on a time axis, one lane per rule.</summary>
public sealed class RetentionTimelineControl : FrameworkElement
{
    private const double LabelWidth = 170;
    private const double LaneHeight = 22;
    private const double AxisHeight = 22;
    private const double MarkerWidth = 3;
    private const double RightPadding = 8;

    private static readonly Brush[] Palette =
    [
        Frozen(0x4E, 0x79, 0xA7), Frozen(0xF2, 0x8E, 0x2B), Frozen(0x59, 0xA1, 0x4F), Frozen(0xE1, 0x57, 0x59),
        Frozen(0x76, 0xB7, 0xB2), Frozen(0xED, 0xC9, 0x48), Frozen(0xB0, 0x7A, 0xA1), Frozen(0x9C, 0x75, 0x5F),
    ];
    private static readonly Pen LanePen = FrozenPen(Color.FromRgb(0xDD, 0xDD, 0xDD));
    private static readonly Pen AxisPen = FrozenPen(Colors.Gray);
    private static readonly Typeface TextFace = new("Segoe UI");

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

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var totalDays = (to - from).TotalDays;
        double X(DateTime time) => LabelWidth + Math.Clamp((time - from).TotalDays / totalDays, 0, 1) * plotWidth;

        for (var i = 0; i < lanes.Count; i++)
        {
            var top = i * LaneHeight;
            var middle = top + LaneHeight / 2;
            var label = Text(lanes[i].Label, 12, Brushes.Black, pixelsPerDip);
            label.MaxTextWidth = LabelWidth - 8;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(label, new Point(0, middle - label.Height / 2));
            drawingContext.DrawLine(LanePen, new Point(LabelWidth, middle), new Point(LabelWidth + plotWidth, middle));

            var brush = Palette[i % Palette.Length];
            foreach (var time in lanes[i].Times)
            {
                drawingContext.DrawRectangle(brush, null,
                    new Rect(X(time) - MarkerWidth / 2, top + 4, MarkerWidth, LaneHeight - 8));
            }
        }

        var axisTop = lanes.Count * LaneHeight;
        drawingContext.DrawLine(AxisPen, new Point(LabelWidth, axisTop), new Point(LabelWidth + plotWidth, axisTop));

        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        var pixelsPerMonth = plotWidth / months;
        var step = pixelsPerMonth >= 50 ? 1 : pixelsPerMonth >= 17 ? 3 : pixelsPerMonth >= 9 ? 6 : 12;
        var labelFormat = step == 12 ? "yyyy" : "MMM yy";
        for (var tick = new DateTime(from.Year, from.Month, 1).AddMonths(1); tick <= to; tick = tick.AddMonths(1))
        {
            if ((tick.Month - 1) % step != 0)
                continue;
            var x = X(tick);
            drawingContext.DrawLine(AxisPen, new Point(x, axisTop), new Point(x, axisTop + 4));
            var text = Text(tick.ToString(labelFormat, CultureInfo.CurrentCulture), 10, Brushes.Gray, pixelsPerDip);
            if (x + 2 + text.Width <= ActualWidth)
                drawingContext.DrawText(text, new Point(x + 2, axisTop + 5));
        }
    }

    private static FormattedText Text(string text, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, size, brush, pixelsPerDip);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();
        return pen;
    }
}
