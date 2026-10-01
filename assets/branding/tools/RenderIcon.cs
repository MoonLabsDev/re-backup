using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class P
{
    [STAThread]
    static void Main(string[] args)
    {
        var outDir = args[0];
        foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
                Draw(dc, size);
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var f = File.Create(Path.Combine(outDir, $"icon-{size}.png"));
            enc.Save(f);
        }
    }

    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    // 64x64 design units. Small sizes get thicker strokes and drop the label.
    static void Draw(DrawingContext dc, int size)
    {
        var small = size <= 24;
        dc.DrawRoundedRectangle(B("#14181D"), new Pen(B("#2A313B"), small ? 0 : 1.5), new Rect(2, 2, 60, 60), 14, 14);
        var teal = B("#2BB3A3");
        var arc = new Pen(teal, small ? 7 : 5.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, arc, Geometry.Parse("M51 33 A19 19 0 1 1 45.5 19.6"));
        dc.DrawGeometry(null, arc, Geometry.Parse("M46.5 9.5 V20.5 H35.5"));
        dc.DrawRoundedRectangle(B("#E6EAF0"), null, new Rect(20, 25, 24, 16), 3.5, 3.5);
        dc.DrawEllipse(teal, null, new Point(38.5, 33), small ? 2.6 : 2.1, small ? 2.6 : 2.1);
        if (!small)
        {
            var text = new FormattedText("RE", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), 8.2, B("#0F1216"), 1.0);
            dc.DrawText(text, new Point(22.4, 27.6));
        }
    }
}
