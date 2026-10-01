using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ReBackup.App.Controls;

/// <summary>
/// A round progress indicator with content in its centre (a percentage, a glyph). Draws a full-circle track
/// (<see cref="Track"/>, optionally dashed and faded) and on it an arc of <see cref="Value"/> (0..1) clockwise from the
/// top in <see cref="Stroke"/>; 1 draws the whole circle. While <see cref="IsIndeterminate"/> a short arc spins instead.
/// The brushes are normally theme brushes set as DynamicResource, so a theme switch redraws the ring.
/// Its template (Theme/Controls.xaml) centres the content.
/// The spin does not re-render: the arc is drawn once under a RotateTransform whose angle is animated, and the
/// animation runs only while the ring is visible (not while its window is hidden to the tray).
/// </summary>
public sealed class ProgressRing : ContentControl
{
    /// <summary>Length of the spinning arc, as a share of the circle.</summary>
    private const double SpinArc = 0.22;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((ProgressRing)d).UpdateSpin()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackDashArrayProperty = DependencyProperty.Register(
        nameof(TrackDashArray), typeof(DoubleCollection), typeof(ProgressRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackOpacityProperty = DependencyProperty.Register(
        nameof(TrackOpacity), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Turns the spinning arc; only its Angle is animated, so a spin step needs no OnRender.</summary>
    private readonly RotateTransform _spin = new();
    private bool _spinning;

    public ProgressRing()
    {
        Loaded += (_, _) => UpdateSpin();
        Unloaded += (_, _) => UpdateSpin();
        IsVisibleChanged += (_, _) => UpdateSpin();
    }

    /// <summary>Progress, 0..1.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    /// <summary>Brush of the progress arc.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Brush of the full circle under the arc; null for none.</summary>
    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>Width of the ring.</summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>Dashes of the track in multiples of <see cref="Thickness"/> ("1 1"); null for a solid track.</summary>
    public DoubleCollection? TrackDashArray
    {
        get => (DoubleCollection?)GetValue(TrackDashArrayProperty);
        set => SetValue(TrackDashArrayProperty, value);
    }

    public double TrackOpacity
    {
        get => (double)GetValue(TrackOpacityProperty);
        set => SetValue(TrackOpacityProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var thickness = Thickness;
        var radius = (size - thickness) / 2;
        if (radius <= 0)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        if (Track is { } track)
        {
            var pen = new Pen(track, thickness) { DashCap = PenLineCap.Flat };   // square caps would close the gaps
            if (TrackDashArray is { Count: > 0 } dashes)
                pen.DashStyle = new DashStyle(dashes, 0);
            drawingContext.PushOpacity(Math.Clamp(TrackOpacity, 0, 1));
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            drawingContext.Pop();
        }

        if (Stroke is not { } stroke)
            return;
        if (IsIndeterminate)
        {
            // Drawn once per layout or property change; the animation turns it through _spin.
            var spinPen = new Pen(stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _spin.CenterX = center.X;
            _spin.CenterY = center.Y;
            drawingContext.PushTransform(_spin);
            drawingContext.DrawGeometry(null, spinPen, Arc(center, radius, -90, SpinArc * 360));
            drawingContext.Pop();
            return;
        }

        var value = Math.Clamp(Value, 0, 1);
        var arcPen = new Pen(stroke, thickness);
        if (value >= 1)
            drawingContext.DrawEllipse(null, arcPen, center, radius, radius);
        else if (value > 0)
            drawingContext.DrawGeometry(null, arcPen, Arc(center, radius, -90, value * 360));
    }

    /// <summary>An arc from <paramref name="startDegrees"/> (0 = 3 o'clock) clockwise over <paramref name="sweepDegrees"/>.</summary>
    private static StreamGeometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        static Point On(Point c, double r, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(radians), c.Y + r * Math.Sin(radians));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(On(center, radius, startDegrees), isFilled: false, isClosed: false);
            context.ArcTo(On(center, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0,
                isLargeArc: sweepDegrees > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Spins while indeterminate, loaded and visible; otherwise the animation clock is removed.</summary>
    private void UpdateSpin()
    {
        var spin = IsIndeterminate && IsLoaded && IsVisible;
        if (spin == _spinning)
            return;
        _spinning = spin;
        _spin.BeginAnimation(RotateTransform.AngleProperty, spin
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    /// <summary>For tests and the harness: whether the spin animation is running.</summary>
    internal bool IsSpinning => _spinning;
}
