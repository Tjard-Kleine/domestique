using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Domestique.Core.Routes;

namespace Domestique.App;

// Gradient profile: per section one bar up (average climbing grade, green < 3 %, yellow < 6 %, orange < 9 %, red above)
// and one bar down (average descent, blue). Kept apart because on rolling roads climbs and descents of a section
// would cancel out to "flat". Flat sections are a thin gray line, ridden sections are faded, and a marker shows the
// position with the current grade. Bars are computed once per route and size; each second only the marker moves.
internal sealed class GradientProfile : FrameworkElement
{
    private const double Pitch = 3, Bar = 2;                                 // bar spacing and width
    private const double LabelSpace = 15;                                   // room for the grade label above the marker
    private const double MinGrade = 0.5, MinShare = 0.1;                    // counts from 0.5 % if at least 10 % of the section is like that

    private static readonly Brush Descent = Frozen(0x33, 0x8C, 0xFF), Flat = Frozen(0x7F, 0x7F, 0x7F),
        Easy = Frozen(0x59, 0xBF, 0x59), Moderate = Frozen(0xFF, 0xCC, 0x3F), Hard = Frozen(0xFF, 0x66, 0x39), Steep = Frozen(0xFF, 0x3B, 0x30);

    private Route? _route;
    private (double Up, double Down)[] _bins = [];
    private double _width = -1, _done, _grade;

    public void SetRoute(Route? route)
    {
        _route = route;
        _width = -1;                                                         // recompute the bars
        _done = 0;
        InvalidateVisual();
    }

    // Ridden distance in meters and grade at that point; redraws only on a visible change.
    public void SetPosition(double distanceM, double gradePercent)
    {
        if (Math.Abs(distanceM - _done) < 1 && Math.Abs(gradePercent - _grade) < 0.05) return;
        _done = distanceM;
        _grade = gradePercent;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_route is not { } route || ActualWidth < Pitch * 4 || ActualHeight <= LabelSpace + 4) return;
        double w = ActualWidth, h = ActualHeight - LabelSpace;
        if (_width != w) { _bins = Bins(route, (int)((w + Pitch - Bar) / Pitch)); _width = w; }

        // Zero line so the steepest climb and descent both fit; flat routes stay flat
        double up = Math.Max(4, _bins.Max(b => b.Up)), down = _bins.Max(b => b.Down);
        double scale = h / (up + down), zero = LabelSpace + up * scale;
        int doneBars = (int)(Math.Clamp(_done / route.LengthM, 0, 1) * _bins.Length);
        double span = _bins.Length * Pitch - (Pitch - Bar), left = (w - span) / 2;

        for (int i = 0; i < _bins.Length; i++)
        {
            var (climb, fall) = _bins[i];
            double x0 = left + i * Pitch;
            if (i < doneBars) dc.PushOpacity(0.35);
            if (climb > 0) dc.DrawRoundedRectangle(BrushFor(climb), null, new Rect(x0, zero - Math.Max(1.5, climb * scale), Bar, Math.Max(1.5, climb * scale)), 0.8, 0.8);
            if (fall > 0) dc.DrawRoundedRectangle(Descent, null, new Rect(x0, zero, Bar, Math.Max(1.5, fall * scale)), 0.8, 0.8);
            if (climb == 0 && fall == 0) dc.DrawRectangle(Flat, null, new Rect(x0, zero - 0.75, Bar, 1.5));   // flat: thin line
            if (i < doneBars) dc.Pop();
        }

        // Position and current grade
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        double x = left + Math.Clamp(_done / route.LengthM, 0, 1) * span;
        dc.DrawRectangle(text, null, new Rect(x - 0.75, LabelSpace - 1, 1.5, ActualHeight - LabelSpace + 1));
        var label = new FormattedText($"{_grade:+0.0;-0.0;0.0}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(TryFindResource("HudFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double lx = Math.Clamp(x - label.Width / 2, 0, w - label.Width);
        dc.DrawText(label, new Point(lx, LabelSpace - 3 - label.Baseline));
    }

    // Per section: average grade of the climbing parts and of the descending parts (positive), from the smoothed 10 m grades.
    // Only parts from 0.5 % count, and only when they make up at least 10 % of the section, so single spikes add no bar.
    private static (double Up, double Down)[] Bins(Route route, int count)
    {
        var points = route.Points;
        count = Math.Clamp(count, 1, points.Count);
        var bins = new (double, double)[count];
        for (int i = 0; i < count; i++)
        {
            int from = i * points.Count / count, to = (i + 1) * points.Count / count;
            double upSum = 0, downSum = 0;
            int ups = 0, downs = 0;
            for (int k = from; k < to; k++)
            {
                double g = points[k].GradePercent;
                if (g >= MinGrade) { upSum += g; ups++; }
                else if (g <= -MinGrade) { downSum -= g; downs++; }
            }
            int n = Math.Max(1, to - from);
            bins[i] = (ups >= n * MinShare ? upSum / ups : 0, downs >= n * MinShare ? downSum / downs : 0);
        }
        return bins;
    }

    private static Brush BrushFor(double climb) => climb < 3 ? Easy : climb < 6 ? Moderate : climb < 9 ? Hard : Steep;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
