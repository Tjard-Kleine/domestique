using System.Windows;
using System.Windows.Media;

namespace Domestique.App;

// Symbole der Übersicht nach dem Figma-Entwurf, sauber nachgezeichnet. Jedes liegt in der Box seines Figma-Vorbilds
// (Breite × Höhe in den Kommentaren) und wird über Fill eingefärbt und über Stretch skaliert.
internal static class Icons
{
    private const double Fine = 0.004;                                      // Toleranz beim Umrechnen: Boxen sind nur ~11 Einheiten groß

    // Blitz neben der Wattzahl (16,4 × 25,9): oben breit mit gerader linker Kante, Fahne nach rechts, Spitze unten
    public static readonly Geometry Bolt = Frozen(() => Rounded(
        Polygon((2.4, 0), (11.9, 0), (9.7, 9.7), (17.2, 9.7), (5.4, 25.9), (7.6, 14.6), (1.1, 14.6), (1.1, 1.3)), 0.8));

    // Herz mit EKG-Linie (11 × 9,9): Ausschlag nach oben, dann ein V nach unten, aus dem Herz ausgeschnitten
    public static readonly Geometry HeartPulse = Frozen(() =>
    {
        var heart = Geometry.Parse("M5.5,9.9 C2.6,7.6 0,5.6 0,3.0 C0,1.3 1.3,0 3.0,0 C4.1,0 5.0,0.6 5.5,1.5 C6.0,0.6 6.9,0 8.0,0 C9.7,0 11,1.3 11,3.0 C11,5.6 8.4,7.6 5.5,9.9 Z");
        var pulse = Line(1.15, (-1, 4.6), (2.4, 4.6), (3.8, 2.2), (5.5, 6.8), (7.0, 3.9), (7.9, 4.6), (12, 4.6));
        return new CombinedGeometry(GeometryCombineMode.Exclude, heart, pulse);
    });

    // Kurbel (11,6 × 11,6): offenes Kettenblatt, Kurbelarm nach rechts oben, unten links das Pedal
    public static readonly Geometry Crank = Frozen(() =>
    {
        var hub = new Point(5.6, 6.0);
        var ring = new EllipseGeometry(hub, 3.3, 3.3).GetWidenedPathGeometry(new Pen(Brushes.Black, 1.6), Fine, ToleranceType.Absolute);
        var gap = Line(3.3, (hub.X, hub.Y), (12, -0.4));                   // Lücke im Blatt, wo der Arm hinausgeht
        var arm = Line(1.7, (hub.X, hub.Y), (10.6, 1.0));
        var pedal = Rounded(Polygon((1.25, 9.05), (2.5, 10.3), (1.25, 11.55), (0, 10.3)), 0.5);
        return Union(new CombinedGeometry(GeometryCombineMode.Exclude, ring, gap), arm, pedal);
    });

    // Tacho (10,2 × 7,2): Bogen oben herum, Nadel nach rechts oben, Nabe unten in der Mitte
    public static readonly Geometry Speed = Frozen(() =>
    {
        var c = new Point(5.08, 5.0);
        const double r = 4.25;
        Point At(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y - r * Math.Sin(deg * Math.PI / 180));
        var arc = new PathGeometry([new PathFigure(At(200), [new ArcSegment(At(-20), new Size(r, r), 0, true, SweepDirection.Clockwise, true)], false)]);
        return Union(arc.GetWidenedPathGeometry(RoundPen(1.5), Fine, ToleranceType.Absolute), Line(1.4, (c.X, c.Y), (7.7, 2.3)), new EllipseGeometry(c, 1.15, 1.15));
    });

    // Strecke (10,4 × 7,9): Start unten links, Ziel oben rechts, dazwischen der Weg im Zickzack
    public static readonly Geometry Distance = Frozen(() => Union(
        new EllipseGeometry(new Point(1.74, 6.2), 1.74, 1.74),
        new EllipseGeometry(new Point(8.68, 1.74), 1.74, 1.74),
        Line(1.45, (1.74, 6.2), (3.4, 1.3), (6.9, 6.6), (8.68, 1.74))));

    // Höhenmeter (9,7 × 8,4): großer Gipfel links, kleiner rechts
    public static readonly Geometry Climb = Frozen(() =>
        Rounded(Polygon((0, 8.43), (3.72, 0), (6.66, 5.9), (7.66, 3.75), (9.67, 8.43)), 0.7));

    // Uhr (10,4 × 10,4): Ring mit Stunden- und Minutenzeiger
    public static readonly Geometry Clock = Frozen(() => Union(
        new EllipseGeometry(new Point(5.21, 5.21), 4.6, 4.6).GetWidenedPathGeometry(new Pen(Brushes.Black, 1.2), Fine, ToleranceType.Absolute),
        Line(1.15, (5.21, 2.6), (5.21, 5.4), (7.0, 6.7))));

    private static Pen RoundPen(double width) => new(Brushes.Black, width)
    {
        StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
    };

    private static Geometry Polygon(params (double X, double Y)[] points)
    {
        var figure = new PathFigure(new Point(points[0].X, points[0].Y),
            points.Skip(1).Select(p => (PathSegment)new LineSegment(new Point(p.X, p.Y), true)), true);
        return new PathGeometry([figure]);
    }

    // Linienzug mit runden Enden und Ecken als Fläche
    private static Geometry Line(double width, params (double X, double Y)[] points)
    {
        var figure = new PathFigure(new Point(points[0].X, points[0].Y),
            points.Skip(1).Select(p => (PathSegment)new LineSegment(new Point(p.X, p.Y), true)), false);
        return new PathGeometry([figure]).GetWidenedPathGeometry(RoundPen(width), Fine, ToleranceType.Absolute);
    }

    // Fläche mit leicht gerundeten Ecken: Umriss mit runder Kante dazu
    private static Geometry Rounded(Geometry shape, double radius) =>
        Union(shape, shape.GetWidenedPathGeometry(RoundPen(radius), Fine, ToleranceType.Absolute));

    private static Geometry Union(params Geometry[] parts) =>
        parts.Skip(1).Aggregate(parts[0], (a, b) => new CombinedGeometry(GeometryCombineMode.Union, a, b));

    private static Geometry Frozen(Func<Geometry> make)
    {
        var g = make().GetOutlinedPathGeometry(Fine, ToleranceType.Absolute);  // einmal zu einer Fläche verschmelzen
        g.Freeze();
        return g;
    }
}
