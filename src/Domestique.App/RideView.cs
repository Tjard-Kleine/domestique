using System.Windows;
using System.Windows.Media;
using static Domestique.App.Co;

namespace Domestique.App;

// Strecke geladen: Der Avatar gleitet nach rechts, unter ihm läuft ein leicht holpriger Weg aus dem Höhenprofil der
// neuen Strecke durch, er fährt ein paar Sekunden darüber (wippt, neigt sich mit der Steigung, etwas Staub hinter ihm)
// und gleitet zurück auf seinen Platz. Läuft nur während der Animation, danach ist die Ansicht weg.
internal sealed class RideView : FrameworkElement
{
    private const double OutMs = 550, RideMs = 2600, BackMs = 550;

    private readonly FrameLoop _loop;
    private double _start, _hh, _rideX, _baseline, _amp, _period;
    private double[] _heights = [0];
    private Rect _area;
    private Point _home;
    private AvatarPose _pose = new();
    private Avatar? _from;
    private Action? _done;
    private (Point Center, double Tilt, double Track, double Scroll, AvatarPose Pose) _frame;

    public bool Running { get; private set; }
    public event Action? Returning;                                          // gleitet zurück: Übersicht wieder aufhellen
    private bool _returning;

    public RideView()
    {
        IsHitTestVisible = false;
        _loop = new FrameLoop(Tick);
    }

    // heights: Höhenprofil der Strecke, 0 … 1 (flache Strecken bekommen trotzdem ein paar sanfte Wellen)
    public void Start(Avatar from, Rect area, double[] heights, Action? done = null)
    {
        if (Running) return;
        if (!Motion.Enabled) { done?.Invoke(); return; }
        Visibility = Visibility.Visible;
        UpdateLayout();
        var (c, hh) = from.Body;
        _home = from.TranslatePoint(c, this);
        _hh = hh;
        _pose = from.CurrentPose().Clone();
        _from = from;
        _done = done;
        _area = area;
        _heights = heights.Length > 1 ? heights : [0, 0];
        _rideX = area.Left + area.Width * 0.55;
        _baseline = area.Top + area.Height * 0.84;
        _amp = hh * 0.75;
        _period = area.Width * 1.6;
        from.Opacity = 0;
        _start = _loop.Now;
        _returning = false;
        Running = true;
        _loop.Start();
    }

    private bool Tick(double dt, double now)
    {
        double t = now - _start;
        double scroll = Math.Max(0, t - OutMs) / 1000 * _area.Width * 0.45;   // der Weg läuft nach links durch
        var ride = new Point(_rideX, TrackY(_rideX, scroll) - _hh * 1.02);
        var pose = _pose.Clone();
        Point center;
        double tilt = 0, track;
        if (t < OutMs)                                                       // nach rechts gleiten
        {
            double k = InOut(t / OutMs);
            center = Arc(_home, ride, k);
            pose.Sx *= 1 + 0.1 * Math.Sin(Math.PI * k);
            pose.Sy *= 1 - 0.06 * Math.Sin(Math.PI * k);
            pose.LookX = Lerp(_pose.LookX, 0.5, k);
            track = 0;
        }
        else if (t < OutMs + RideMs)                                         // fahren
        {
            double rt = (t - OutMs) / 1000;
            double slope = (TrackY(_rideX + 4, scroll) - TrackY(_rideX - 4, scroll)) / 8;
            tilt = Math.Atan(slope) * 0.7;
            double bob = Math.Sin(rt * 2 * Math.PI * 3);                     // tritt in die Pedale
            center = new Point(ride.X, ride.Y + bob * _hh * 0.03);
            pose.Sy *= 1 + bob * 0.025;
            pose.Sx *= 1 - bob * 0.015;
            pose.LookX = 0.5;
            track = Seg(rt, 0, 0.25) * (1 - Seg(rt, RideMs / 1000 - 0.3, RideMs / 1000));
        }
        else                                                                 // zurück auf den Platz
        {
            if (!_returning) { _returning = true; Returning?.Invoke(); }
            double k = InOut(Seg(t - OutMs - RideMs, 0, BackMs));
            center = Arc(ride, _home, k);
            pose.Sx *= 1 + 0.1 * Math.Sin(Math.PI * k);
            pose.Sy *= 1 - 0.06 * Math.Sin(Math.PI * k);
            pose.LookX = Lerp(0.5, _pose.LookX, k);
            track = 0;
            if (k >= 1)
            {
                Running = false;
                Visibility = Visibility.Collapsed;
                if (_from is not null) _from.Opacity = 1;
                var done = _done;
                _done = null;
                done?.Invoke();
                return false;
            }
        }
        pose.Tilt = tilt;
        _frame = (center, tilt, track, scroll, pose);
        InvalidateVisual();
        return true;
    }

    // kleiner Bogen nach oben, wie beim Gleiten zwischen Bluetooth-Karte und Übersicht
    private Point Arc(Point a, Point b, double k) =>
        new(Lerp(a.X, b.X, k), Lerp(a.Y, b.Y, k) - Math.Sin(Math.PI * k) * _hh * 0.35);

    // Höhe des Wegs an Bildschirmstelle x: Höhenprofil, zyklisch, plus leichte Holprigkeit
    private double TrackY(double x, double scroll)
    {
        double wx = x - _area.Left + scroll;
        double u = (wx / _period % 1 + 1) % 1 * _heights.Length;
        int i = (int)u;
        double f = (1 - Math.Cos((u - i) * Math.PI)) / 2;
        double h = Lerp(_heights[i % _heights.Length], _heights[(i + 1) % _heights.Length], f);
        h = h * 0.8 + 0.12 * Math.Sin(wx * 0.045) + 0.06 * Math.Sin(wx * 0.13 + 1);
        return _baseline - h * _amp;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Running || _frame.Pose is null) return;
        var (center, _, track, scroll, pose) = _frame;
        if (track > 0.01)
        {
            var line = new StreamGeometry();
            var fill = new StreamGeometry();
            using (var l = line.Open())
            using (var a = fill.Open())
            {
                a.BeginFigure(new Point(_area.Left, _area.Bottom), true, true);
                for (double x = _area.Left; x <= _area.Right; x += 4)
                {
                    var p = new Point(x, TrackY(x, scroll));
                    if (x == _area.Left) l.BeginFigure(p, false, false); else l.LineTo(p, true, true);
                    a.LineTo(p, true, false);
                }
                a.LineTo(new Point(_area.Right, _area.Bottom), true, false);
            }
            var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
            var accent = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.SteelBlue;
            dc.PushClip(new RectangleGeometry(_area, 12, 12));
            dc.PushOpacity(track);
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(90, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90),
                            null, fill);
            dc.DrawGeometry(null, new Pen(text, 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
            // Staub hinter ihm
            double hw = _hh * AvatarArt.Aspect;
            double rt = scroll / (_area.Width * 0.45);
            for (int k = 0; k < 5; k++)
            {
                double age = (rt * 1.8 + k / 5.0) % 1;
                double x = center.X - hw - age * hw * 2.2;
                dc.PushOpacity((1 - age) * 0.6);
                dc.DrawEllipse(text, null, new Point(x, TrackY(x, scroll) - 3 - age * 6), 1.4, 1.4);
                dc.Pop();
            }
            dc.Pop();
            dc.Pop();
        }
        AvatarArt.Draw(dc, pose, center, _hh, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
