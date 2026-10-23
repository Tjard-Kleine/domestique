using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Domestique.Core.Routes;

namespace Domestique.App;

// Steigungsprofil statt Höhenprofil: ein Balken je Abschnitt, bergauf nach oben, bergab nach unten, gefärbt nach
// Steilheit (flach grau, bis 3 % grün, bis 6 % gelb, bis 9 % orange, darüber rot, bergab blau). Gefahrene Abschnitte
// sind blass. Eine Linie markiert die Position, darüber steht die aktuelle Steigung (früher oben rechts in der Karte).
// Die Balken werden einmal je Strecke und Größe berechnet; pro Sekunde wandert nur die Markierung.
internal sealed class GradientProfile : FrameworkElement
{
    private const double Pitch = 4, Bar = 3;                                 // Abstand und Breite der Balken
    private const double LabelSpace = 15;                                   // oben Platz für die Steigung an der Markierung

    private static readonly Brush Descent = Frozen(0x33, 0x8C, 0xFF), Flat = Frozen(0x7F, 0x7F, 0x7F),
        Easy = Frozen(0x59, 0xBF, 0x59), Moderate = Frozen(0xFF, 0xCC, 0x3F), Hard = Frozen(0xFF, 0x66, 0x39), Steep = Frozen(0xFF, 0x3B, 0x30);

    private Route? _route;
    private double[] _grades = [];
    private double _width = -1, _done, _grade;

    public void SetRoute(Route? route)
    {
        _route = route;
        _width = -1;                                                         // Balken neu berechnen
        _done = 0;
        InvalidateVisual();
    }

    // gefahrene Strecke in Metern und Steigung an dieser Stelle; zeichnet nur neu, wenn sich etwas sichtbar ändert
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
        if (_width != w) { _grades = Bins(route, (int)((w + Pitch - Bar) / Pitch)); _width = w; }

        // Nulllinie so, dass die steilste Rampe und das steilste Gefälle hineinpassen; flache Strecken bleiben flach
        double up = Math.Max(4, _grades.Max()), down = Math.Max(0, -_grades.Min());
        if (down < 1) down = 0;
        double scale = h / (up + down), zero = LabelSpace + up * scale;
        int doneBars = (int)(Math.Clamp(_done / route.LengthM, 0, 1) * _grades.Length);
        double left = (w - (_grades.Length * Pitch - (Pitch - Bar))) / 2;

        for (int i = 0; i < _grades.Length; i++)
        {
            double g = Math.Clamp(_grades[i], -down, up);
            double size = Math.Max(1.5, Math.Abs(g) * scale);                // flache Abschnitte als dünner Strich
            var rect = g >= 0 ? new Rect(left + i * Pitch, zero - size, Bar, size) : new Rect(left + i * Pitch, zero, Bar, size);
            if (i < doneBars) dc.PushOpacity(0.35);
            dc.DrawRoundedRectangle(BrushFor(_grades[i]), null, rect, 1, 1);
            if (i < doneBars) dc.Pop();
        }

        // Position und aktuelle Steigung
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        double x = left + Math.Clamp(_done / route.LengthM, 0, 1) * (_grades.Length * Pitch - (Pitch - Bar));
        dc.DrawRectangle(text, null, new Rect(x - 0.75, LabelSpace - 1, 1.5, ActualHeight - LabelSpace + 1));
        var label = new FormattedText($"{_grade:+0.0;-0.0;0.0}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(TryFindResource("HudFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double lx = Math.Clamp(x - label.Width / 2, 0, w - label.Width);
        dc.DrawText(label, new Point(lx, LabelSpace - 3 - label.Baseline));
    }

    // Steigung je Abschnitt aus der Höhe am Anfang und Ende des Abschnitts
    private static double[] Bins(Route route, int count)
    {
        count = Math.Max(1, count);
        double step = route.LengthM / count;
        var grades = new double[count];
        for (int i = 0; i < count; i++)
        {
            double a = route.At(i * step).ElevationM, b = route.At((i + 1) * step).ElevationM;
            grades[i] = (b - a) / Math.Max(step, 1) * 100;
        }
        return grades;
    }

    private static Brush BrushFor(double g) =>
        g < -0.5 ? Descent : g < 0.5 ? Flat : g < 3 ? Easy : g < 6 ? Moderate : g < 9 ? Hard : Steep;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
