using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Islet.Ui;

/// <summary>Damped spring easing, similar to iOS spring animations (slight overshoot, quick settle).</summary>
public sealed class SpringEase : EasingFunctionBase
{
    public double Damping { get; set; } = 0.68;   // zeta (lower = bouncier)
    public double Stiffness { get; set; } = 8.0;  // natural frequency over the normalized duration

    public SpringEase() { EasingMode = EasingMode.EaseIn; } // EaseIn => our curve is used as-is

    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        double z = Damping, w0 = Stiffness;
        double wd = w0 * Math.Sqrt(1 - z * z);
        return 1 - Math.Exp(-z * w0 * t) * (Math.Cos(wd * t) + (z * w0 / wd) * Math.Sin(wd * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}

public static class Bars
{
    /// <summary>Builds an animated waveform (equalizer) into a panel and returns its storyboard.</summary>
    public static Storyboard Build(Panel host, int count, double barW, double gap, double maxH, Brush brush)
    {
        host.Children.Clear();
        var sb = new Storyboard();
        Timeline.SetDesiredFrameRate(sb, 30); // the bars don't need 60+ fps -> less GPU/CPU
        double[] durs = { 0.42, 0.56, 0.36, 0.49, 0.45 };
        double[] lows = { 0.30, 0.22, 0.38, 0.26, 0.3 };
        for (int i = 0; i < count; i++)
        {
            var r = new Rectangle
            {
                Width = barW, Height = maxH, RadiusX = barW / 2, RadiusY = barW / 2, Fill = brush,
                Margin = i > 0 ? new Thickness(gap, 0, 0, 0) : default,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 0.3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            host.Children.Add(r);
            var a = new DoubleAnimation(lows[i % lows.Length], 1.0, TimeSpan.FromSeconds(durs[i % durs.Length]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(a, r);
            Storyboard.SetTargetProperty(a, new PropertyPath("RenderTransform.ScaleY"));
            sb.Children.Add(a);
        }
        return sb;
    }
}

/// <summary>iOS-style battery glyph.</summary>
public sealed class BatteryGlyph : Grid
{
    readonly Border _fill;
    const double Inner = 24 - 2.4 - 3.0;

    public BatteryGlyph()
    {
        VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var stroke = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        _fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.8), Background = Brushes.White };
        var body = new Border
        {
            Width = 24, Height = 12, CornerRadius = new CornerRadius(3.6),
            BorderThickness = new Thickness(1.2), BorderBrush = stroke, Padding = new Thickness(1.5),
            Child = _fill,
        };
        var nub = new Border
        {
            Width = 1.8, Height = 4, Margin = new Thickness(1, 0, 0, 0), CornerRadius = new CornerRadius(0, 1, 1, 0),
            Background = stroke, VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(body);
        row.Children.Add(nub);
        Children.Add(row);
    }

    public void Set(int percent, bool charging)
    {
        percent = Math.Clamp(percent, 0, 100);
        _fill.Width = Math.Max(1.6, Inner * percent / 100.0);
        _fill.Background = new SolidColorBrush(charging ? Palette.Green : percent <= 20 ? Palette.Red : Colors.White);
    }
}

public static class Ring
{
    /// <summary>Clockwise arc starting at 12 o'clock covering <paramref name="frac"/> of a circle.</summary>
    public static Geometry Arc(double size, double thickness, double frac)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        if (frac <= 0.0001) return Geometry.Empty;
        double r = (size - thickness) / 2, c = size / 2, a = frac * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new PathFigure(start, new[] { new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true) }, false);
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }
}

public static class Palette
{
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);

    /// <summary>Picks a vivid accent colour from album art (weighted toward saturated pixels).</summary>
    public static Color Accent(BitmapSource src)
    {
        try
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            double R = 0, G = 0, B = 0, W = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                ToHsv(r, g, b, out _, out double s, out double v);
                double wt = s * s * v + 0.01;
                R += r * wt; G += g * wt; B += b * wt; W += wt;
            }
            ToHsv(R / W, G / W, B / W, out double hh, out double ss, out double vv);
            if (ss < 0.12) return Color.FromRgb(0xEE, 0xEE, 0xF2);
            ss = Math.Min(1, ss * 1.2);
            vv = Math.Max(vv, 0.88);
            return FromHsv(hh, ss, vv);
        }
        catch { return Colors.White; }
    }

    static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max; s = max <= 0 ? 0 : d / max; h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
