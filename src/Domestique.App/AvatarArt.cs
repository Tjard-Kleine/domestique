using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Domestique.App;

public enum Mood { Idle, Search, Sleep, Happy, Dizzy, Focus, Strain, Content, Squint, Love }

// Busy: searching ("…"), Done: connected or loaded (green check), Warning: control lost (!), Settings: gear
internal enum BadgeKind { None, Busy, Done, Warning, Settings }

// Everything that makes up one avatar frame. Lengths are multiples of the half body height (hh).
internal sealed class AvatarPose
{
    public Mood Face = Mood.Idle;
    public double Open = 1, EyeScale = 1, LookX, LookY;
    public double Sx = 1, Sy = 1, Ox, Oy, Tilt;          // squash, offset, tilt (rad)
    public double HandL, HandR, Wave = -1;               // hands 0…1; wave: seconds since start, −1 = none
    public double Halo = 0.6, HaloSpread = 0.72;        // glow strength and size (1 = as wide as at launch)
    public double Tint, Badge, Fx = -1;                 // Fx: seconds since hearts started, −1 = none
    public Color HaloColor = Colors.White, TintColor = Color.FromRgb(127, 180, 234);
    public BadgeKind BadgeKind;

    public AvatarPose Clone() => (AvatarPose)MemberwiseClone();

    // Smooth blend of two poses, e.g. from the launch avatar into the overview avatar.
    public static AvatarPose Lerp(AvatarPose a, AvatarPose b, double k)
    {
        static double L(double x, double y, double t) => x + (y - x) * t;
        static Color C(Color x, Color y, double t) => Color.FromArgb((byte)L(x.A, y.A, t), (byte)L(x.R, y.R, t), (byte)L(x.G, y.G, t), (byte)L(x.B, y.B, t));
        var p = a.Clone();
        p.Face = k < 0.5 ? a.Face : b.Face;
        p.Open = L(a.Open, b.Open, k); p.EyeScale = L(a.EyeScale, b.EyeScale, k);
        p.LookX = L(a.LookX, b.LookX, k); p.LookY = L(a.LookY, b.LookY, k);
        p.Sx = L(a.Sx, b.Sx, k); p.Sy = L(a.Sy, b.Sy, k); p.Ox = L(a.Ox, b.Ox, k); p.Oy = L(a.Oy, b.Oy, k); p.Tilt = L(a.Tilt, b.Tilt, k);
        p.HandL = L(a.HandL, b.HandL, k); p.HandR = L(a.HandR, b.HandR, k);
        p.Halo = L(a.Halo, b.Halo, k); p.HaloSpread = L(a.HaloSpread, b.HaloSpread, k); p.HaloColor = C(a.HaloColor, b.HaloColor, k);
        p.Tint = L(a.Tint, b.Tint, k); p.TintColor = C(a.TintColor, b.TintColor, k);
        p.BadgeKind = b.BadgeKind != BadgeKind.None ? b.BadgeKind : a.BadgeKind;
        p.Badge = L(a.BadgeKind == p.BadgeKind ? a.Badge : 0, b.Badge, k);
        return p;
    }
}

// Draws the companion: a soft white body with a light glow, floating hands (round left, long right),
// oval eyes with a highlight and rosy cheeks.
internal static class AvatarArt
{
    public const double Aspect = 1.277;                              // body width to body height

    private static readonly Brush White = Frozen(new LinearGradientBrush(
        new GradientStopCollection { new(Color.FromRgb(252, 252, 253), 0), new(Color.FromRgb(232, 234, 238), 1) }, new Point(0.75, 0), new Point(0.25, 1)));
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)), 1));
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1A)));
    private static readonly Brush Shine = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 255, 255, 255)));
    private static readonly Brush Sheen = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)));
    private static readonly Brush Cheek = Frozen(new SolidColorBrush(Color.FromArgb(0x7A, 0xFF, 0x9E, 0xA8)));
    private static readonly Brush SweatBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xA8, 0xD8, 0xFF)));
    private static readonly Brush HeartBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x7E)));
    private static readonly Brush Ring = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0F)));
    private static readonly Brush BusyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0xA0, 0xF5)));
    private static readonly Brush BusyDots = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x1B, 0x3A)));
    private static readonly Brush DoneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly Brush WarningBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24)));
    private static readonly Brush GearBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9C)));
    private static readonly Brush ZBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xB8, 0xC0, 0xD0)));

    // Body size inside a w × h box, and its center.
    public static double HalfHeight(double w, double h) => Math.Min(h * 0.36, w * 0.39 / Aspect);
    public static Point Center(double w, double h) => new(w / 2, h * 0.53);

    public static void Draw(DrawingContext dc, AvatarPose p, Point c, double hh, double pixelsPerDip)
    {
        if (hh <= 0.4) return;
        double hw = hh * Aspect, hb = hh * 2;
        var at = new Point(c.X + p.Ox * hh, c.Y + p.Oy * hh);
        if (p.Halo > 0.001) DrawHalo(dc, at, hw * p.HaloSpread, p.Halo, p.HaloColor);

        dc.PushTransform(new TranslateTransform(at.X, at.Y));
        dc.PushTransform(new RotateTransform(p.Tilt * 180 / Math.PI));
        dc.PushTransform(new ScaleTransform(p.Sx, p.Sy));

        DrawHands(dc, p, hw, hh, hb);

        var body = new RectangleGeometry(new Rect(-hw, -hh, 2 * hw, 2 * hh), hw * 0.73, hh * 0.85);
        dc.DrawGeometry(White, Edge, body);
        if (p.Tint > 0.002)
        {
            // zone color rises from the bottom
            var tint = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb((byte)(255 * Math.Min(1, p.Tint)), p.TintColor.R, p.TintColor.G, p.TintColor.B), 1),
                new(Color.FromArgb(0, p.TintColor.R, p.TintColor.G, p.TintColor.B), 0.42),
            }, 90);
            dc.DrawGeometry(tint, null, body);
        }
        dc.DrawRoundedRectangle(Sheen, null, new Rect(-hw * 0.62, -hh * 0.86, hw * 0.62, hh * 0.2), hh * 0.1, hh * 0.1);

        DrawFace(dc, p, hw, hh, pixelsPerDip);

        if (p.Badge > 0.01 && p.BadgeKind != BadgeKind.None) DrawBadge(dc, p, hw, hh);
        dc.Pop(); dc.Pop(); dc.Pop();

        if (p.Fx >= 0) DrawHearts(dc, p.Fx, at, hw, hh);
    }

    // Glow: two soft circles, stronger inside
    private static void DrawHalo(DrawingContext dc, Point c, double hw, double halo, Color color)
    {
        foreach (var (radius, alpha) in new[] { (hw * 2.6, 0.18), (hw * 4.2, 0.07) })
        {
            var g = new RadialGradientBrush(Color.FromArgb((byte)(255 * Math.Min(1, alpha * halo)), color.R, color.G, color.B),
                                            Color.FromArgb(0, color.R, color.G, color.B));
            dc.DrawEllipse(g, null, c, radius, radius);
        }
    }

    private static void DrawHands(DrawingContext dc, AvatarPose p, double hw, double hh, double hb)
    {
        double wave = p.Wave;
        if (p.HandL > 0.01)
        {
            double k = p.HandL, r = hb * 0.1 * k;
            double x = Lerp(-hw * 0.35, -hw - hb * 0.14, k), y = Lerp(hh * 0.85, hh * 0.6, k);
            if (wave >= 0) y += Math.Sin(wave * 2 * Math.PI * 5) * hb * 0.12 * Math.Clamp(wave / 0.08, 0, 1);
            dc.DrawEllipse(White, Edge, new Point(x, y), r, r);
        }
        if (p.HandR > 0.01)
        {
            double k = p.HandR, len = hb * 0.3 * k, thick = hb * 0.16 * k;
            double x = Lerp(hw * 0.35, hw + hb * 0.13, k), y = Lerp(hh * 0.85, hh * 0.05, k);
            double angle = -0.52 + (wave >= 0 ? Math.Sin(wave * 2 * Math.PI * 2.5) * 0.05 : 0);
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new RotateTransform(angle * 180 / Math.PI));
            dc.DrawRoundedRectangle(White, Edge, new Rect(-len / 2, -thick / 2, len, thick), thick / 2, thick / 2);
            dc.Pop(); dc.Pop();
        }
    }

    private static void DrawFace(DrawingContext dc, AvatarPose p, double hw, double hh, double pixelsPerDip)
    {
        var face = p.Face;
        double ex = hh * 0.34, ey = hh * 0.04;
        double lx = p.LookX * hw * 0.3 + (face == Mood.Search ? hw * 0.14 : 0), ly = p.LookY * hh * 0.18;
        double stroke = hh * 0.1;
        var pen = new Pen(Ink, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        if (face is Mood.Happy or Mood.Dizzy or Mood.Strain or Mood.Content or Mood.Squint or Mood.Love)
            foreach (int s in new[] { -1, 1 })
                dc.DrawEllipse(Cheek, null, new Point(s * hh * 0.7 + lx * 0.3, hh * 0.36), hh * 0.21, hh * 0.1);

        foreach (int s in new[] { -1, 1 })
        {
            var e = new Point(s * ex + lx, ey + ly);
            switch (face)
            {
                case Mood.Happy:                                               // ∩
                    dc.DrawGeometry(null, pen, Curve(e, -0.19 * hh, 0.07 * hh, 0, -0.24 * hh, 0.19 * hh, 0.07 * hh));
                    break;
                case Mood.Content:
                case Mood.Sleep:                                               // ∪, content or asleep
                    dc.DrawGeometry(null, pen, Curve(e, -0.19 * hh, -0.04 * hh, 0, 0.17 * hh, 0.19 * hh, -0.04 * hh));
                    break;
                case Mood.Squint:                                              // narrow, deeper inside
                    dc.DrawLine(pen, new Point(e.X - s * 0.17 * hh, e.Y - 0.04 * hh), new Point(e.X + s * 0.13 * hh, e.Y + 0.05 * hh));
                    break;
                case Mood.Strain:                                              // > <
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(e.X - s * 0.15 * hh, e.Y - 0.17 * hh), false, false);
                        c.LineTo(new Point(e.X + s * 0.15 * hh, e.Y), true, true);
                        c.LineTo(new Point(e.X - s * 0.15 * hh, e.Y + 0.17 * hh), true, true);
                    }
                    dc.DrawGeometry(null, pen, g);
                    break;
                case Mood.Dizzy:
                    dc.DrawGeometry(null, new Pen(Ink, stroke * 0.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Spiral(e, hh, s));
                    break;
                case Mood.Love:
                    dc.DrawGeometry(HeartBrush, null, Heart(e, hh * 0.2 * p.EyeScale));
                    break;
                default:                                                       // open, with highlight
                    double rx = hh * 0.14 * p.EyeScale, ry = hh * 0.22 * p.EyeScale * (face == Mood.Focus ? 0.75 : 1) * Math.Max(0.06, p.Open);
                    dc.DrawEllipse(Ink, null, e, rx, ry);
                    if (p.Open > 0.5) dc.DrawEllipse(Shine, null, new Point(e.X - rx * 0.32, e.Y - ry * 0.42), rx * 0.34, ry * 0.26);
                    break;
            }
        }

        if (face == Mood.Focus)                                                // determined, not angry
            foreach (int s in new[] { -1, 1 })
                dc.DrawLine(new Pen(Ink, stroke * 0.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new Point(s * hh * 0.17 + lx, -hh * 0.36 + ly), new Point(s * hh * 0.5 + lx, -hh * 0.36 + ly));
        if (face == Mood.Strain)
            dc.DrawGeometry(SweatBrush, null, Drop(new Point(hw * 0.86, -hh * 0.55), hh * 0.17));
        if (face == Mood.Sleep)
        {
            var z = new FormattedText("z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"),
                hh * 0.48, ZBrush, pixelsPerDip);
            dc.DrawText(z, new Point(hw * 0.8, -hh * 1.45));
        }
    }

    private static void DrawBadge(DrawingContext dc, AvatarPose p, double hw, double hh)
    {
        double r = hh * (p.BadgeKind == BadgeKind.Settings ? 0.4 : 0.3) * p.Badge;   // gear slightly larger, otherwise unreadable
        var o = new Point(-hw * 0.78, -hh * 0.74);
        dc.DrawEllipse(Ring, null, o, r + hh * 0.08 * p.Badge, r + hh * 0.08 * p.Badge);
        var white = new Pen(Brushes.White, r * 0.26) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (p.BadgeKind)
        {
            case BadgeKind.Busy:
                dc.DrawEllipse(BusyBrush, null, o, r, r);
                for (int i = -1; i <= 1; i++) dc.DrawEllipse(BusyDots, null, new Point(o.X + i * r * 0.5, o.Y), r * 0.17, r * 0.17);
                break;
            case BadgeKind.Warning:                                        // !
                dc.DrawEllipse(WarningBrush, null, o, r, r);
                dc.DrawLine(white, new Point(o.X, o.Y - r * 0.45), new Point(o.X, o.Y + r * 0.08));
                dc.DrawEllipse(Brushes.White, null, new Point(o.X, o.Y + r * 0.42), r * 0.13, r * 0.13);
                break;
            case BadgeKind.Settings:                                       // gear
                dc.DrawEllipse(GearBrush, null, o, r, r);
                dc.DrawGeometry(Brushes.White, null, Gear(o, r * 0.62, r * 0.46, 8));
                dc.DrawEllipse(GearBrush, null, o, r * 0.2, r * 0.2);
                break;
            default:                                                       // green check
                dc.DrawEllipse(DoneBrush, null, o, r, r);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(o.X - r * 0.45, o.Y + r * 0.02), false, false);
                    c.LineTo(new Point(o.X - r * 0.12, o.Y + r * 0.34), true, true);
                    c.LineTo(new Point(o.X + r * 0.46, o.Y - r * 0.3), true, true);
                }
                dc.DrawGeometry(null, white, g);
                break;
        }
    }

    // Hearts rise and fade (1.8 s).
    private static void DrawHearts(DrawingContext dc, double t, Point c, double hw, double hh)
    {
        for (int i = 0; i < 3; i++)
        {
            double age = t - i * 0.35;
            if (age < 0 || age > 1.2) continue;
            double k = age / 1.2;
            double x = c.X + (i - 1) * hw * 0.55 + Math.Sin(age * 6 + i) * hh * 0.08, y = c.Y - hh * 1.1 - k * hh * 1.1;
            dc.PushOpacity(1 - k);
            dc.DrawGeometry(HeartBrush, null, Heart(new Point(x, y), hh * (0.16 + 0.06 * Math.Sin(Math.PI * k))));
            dc.Pop();
        }
    }

    // Gear with "teeth" teeth: tooth tips outside, ring inside.
    private static StreamGeometry Gear(Point o, double outer, double inner, int teeth)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            int n = teeth * 4;
            for (int i = 0; i < n; i++)
            {
                double a = i * 2 * Math.PI / n, r = (i % 4) is 0 or 1 ? outer : inner;
                var pt = new Point(o.X + Math.Cos(a) * r, o.Y + Math.Sin(a) * r);
                if (i == 0) c.BeginFigure(pt, true, true); else c.LineTo(pt, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    private static StreamGeometry Curve(Point o, double x0, double y0, double cx, double cy, double x1, double y1)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(o.X + x0, o.Y + y0), false, false);
            c.QuadraticBezierTo(new Point(o.X + cx, o.Y + cy), new Point(o.X + x1, o.Y + y1), true, true);
        }
        g.Freeze();
        return g;
    }

    // Spiral of half circles with growing radius, mirrored on the right.
    private static StreamGeometry Spiral(Point o, double hh, int side)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double unit = hh * 0.043, x = o.X;
            var dir = side < 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
            c.BeginFigure(new Point(x, o.Y), false, false);
            for (int i = 1; i <= 4; i++)
            {
                double r = unit * i;
                x += (i % 2 == 1 ? 2 : -2) * r * -side;
                c.ArcTo(new Point(x, o.Y), new Size(r, r), 0, false, dir, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    private static StreamGeometry Heart(Point o, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(o.X, o.Y + r * 0.9), true, true);
            c.BezierTo(new Point(o.X - r * 1.4, o.Y), new Point(o.X - r * 0.9, o.Y - r * 1.1), new Point(o.X, o.Y - r * 0.35), true, true);
            c.BezierTo(new Point(o.X + r * 0.9, o.Y - r * 1.1), new Point(o.X + r * 1.4, o.Y), new Point(o.X, o.Y + r * 0.9), true, true);
        }
        g.Freeze();
        return g;
    }

    private static StreamGeometry Drop(Point o, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(o.X, o.Y - r * 1.4), true, true);
            c.QuadraticBezierTo(new Point(o.X - r * 1.1, o.Y + r * 0.2), new Point(o.X, o.Y + r * 0.7), true, true);
            c.QuadraticBezierTo(new Point(o.X + r * 1.1, o.Y + r * 0.2), new Point(o.X, o.Y - r * 1.4), true, true);
        }
        g.Freeze();
        return g;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
