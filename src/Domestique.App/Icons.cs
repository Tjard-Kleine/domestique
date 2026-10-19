using System.Windows;
using System.Windows.Media;

namespace Domestique.App;

// Kleine Symbole neben Puls und Kadenz, als Vektor gezeichnet (eingefärbt über Fill, skaliert über Stretch)
internal static class Icons
{
    // Herz mit EKG-Linie: Die Linie ist aus dem gefüllten Herz ausgeschnitten
    public static readonly Geometry HeartPulse = Frozen(() =>
    {
        var heart = Geometry.Parse("M8,14 C4,11 0,8 0,4.5 C0,2 2,0 4.4,0 C6,0 7.3,1 8,2.2 C8.7,1 10,0 11.6,0 C14,0 16,2 16,4.5 C16,8 12,11 8,14 Z");
        var pulse = Geometry.Parse("M-1,7 H4.6 L6.2,4.3 L8.2,10 L10,5.8 L11.2,7.6 H17")
            .GetWidenedPathGeometry(new Pen(Brushes.Black, 1.5) { LineJoin = PenLineJoin.Round });
        return new CombinedGeometry(GeometryCombineMode.Exclude, heart, pulse);
    });

    // Kurbel: Kettenblatt mit Zähnen, davor der Kurbelarm mit Pedal; ein schmaler Spalt trennt Arm und Blatt
    public static readonly Geometry Crank = Frozen(() =>
    {
        var center = new Point(10, 6);
        var arm = new LineGeometry(center, new Point(3.6, 12.4));
        Geometry Widen(double width) => arm.GetWidenedPathGeometry(
            new Pen(Brushes.Black, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

        var ring = new CombinedGeometry(GeometryCombineMode.Exclude, Chainring(center, 4.7, 6.3, 10), new EllipseGeometry(center, 2.8, 2.8));   // wenige, hohe Zähne bleiben bei 15 px lesbar
        var gap = new CombinedGeometry(GeometryCombineMode.Union, Widen(5), new RectangleGeometry(new Rect(-0.6, 11, 8.4, 5), 2.5, 2.5));
        var front = new CombinedGeometry(GeometryCombineMode.Union, Widen(2.6), new RectangleGeometry(new Rect(0.6, 12.2, 6, 2.6), 1.3, 1.3));
        var crank = new CombinedGeometry(GeometryCombineMode.Union, new CombinedGeometry(GeometryCombineMode.Exclude, ring, gap), front);
        return new CombinedGeometry(GeometryCombineMode.Exclude, crank, new EllipseGeometry(center, 0.9, 0.9));   // Kurbelschraube
    });

    // Zahnkranz als Polygon: je Zahn Fuß, zwei Spitzenpunkte, Fuß
    private static Geometry Chainring(Point c, double rIn, double rOut, int teeth)
    {
        Point At(double r, double angle) => new(c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle));
        double step = 2 * Math.PI / teeth;
        var points = new List<Point>();
        for (int i = 0; i < teeth; i++)
        {
            double a = -Math.PI / 2 + i * step;
            points.AddRange([At(rIn, a), At(rOut, a + step * 0.12), At(rOut, a + step * 0.42), At(rIn, a + step * 0.54)]);
        }
        var figure = new PathFigure(points[0], points.Skip(1).Select(p => (PathSegment)new LineSegment(p, true)), true);
        return new PathGeometry([figure]);
    }

    private static Geometry Frozen(Func<Geometry> make)
    {
        var g = make();
        g.Freeze();
        return g;
    }
}
