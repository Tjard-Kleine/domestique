using System.Windows;
using System.Windows.Media;
using static Domestique.App.Co;

namespace Domestique.App;

// Strecke geladen: Übergang, der sofort beim Bestätigen beginnt. Der Avatar fährt herein (oder springt aus der
// Übersicht) auf eine gerade weiße Linie, auf der ein paar Steinchen vorbeiziehen: nur weiße Linien, keine Verläufe.
// Danach gleitet er auf seinen Platz in der Übersicht, die dabei erscheint. Läuft nur während der Animation.
internal sealed class RideView : FrameworkElement
{
    private const double InMs = 480, RideMs = 1500, LandMs = 560;

    // Steinchen auf der Linie (Lage 0 … 1, Breite, Höhe) und kleine Kiesel darunter (Lage, Abstand unter der Linie)
    private static readonly (double U, double W, double H)[] Stones = [(0.08, 7, 3.6), (0.31, 4.5, 2.6), (0.55, 9, 4.4), (0.78, 5.5, 3)];
    private static readonly (double U, double Dy)[] Pebbles = [(0.2, 6), (0.47, 8), (0.68, 5.5), (0.93, 7.5)];

    private readonly FrameLoop _loop;
    private double _start, _hh, _lineY, _scroll;
    private Rect _area;
    private Point _from, _ride;
    private bool _drivesIn, _landing;
    private AvatarPose _pose = new();
    private Avatar? _source, _target;
    private (Point Center, double Hh, AvatarPose Pose)? _home;
    private Action? _done;
    private (Point Center, AvatarPose Pose, double Line, double Fade) _frame;

    public bool Running { get; private set; }
    public event Action? Landing;                                            // gleitet auf seinen Platz: Übersicht einblenden

    public RideView()
    {
        IsHitTestVisible = false;
        _loop = new FrameLoop(Tick);
    }

    // from: der sichtbare Avatar, von dem er abspringt; null, wenn er von links hereinfährt (Übersicht gerade nicht zu sehen).
    // to: sein Platz in der Übersicht, area: Fläche der Ansichten in eigenen Koordinaten.
    public void Start(Avatar? from, Avatar to, Rect area, Action? done = null)
    {
        if (Running) return;
        if (!Motion.Enabled) { Landing?.Invoke(); done?.Invoke(); return; }
        Visibility = Visibility.Visible;
        UpdateLayout();
        double hh = to.Body.HalfHeight;
        _hh = hh;
        _area = area;
        _lineY = area.Top + area.Height * 0.8;
        _ride = new Point(area.Left + area.Width * 0.46, _lineY - hh * 1.02);
        _drivesIn = from is null;
        _source = from;
        _target = to;
        _from = from is null ? new Point(area.Left - hh * AvatarArt.Aspect * 1.2, _ride.Y) : from.TranslatePoint(from.Body.Center, this);
        _pose = (from ?? to).CurrentPose().Clone();
        _home = null;
        _landing = false;
        _done = done;
        if (from is not null) from.Opacity = 0;
        to.Opacity = 0;
        _start = _loop.Now;
        Running = true;
        _loop.Start();
    }

    private bool Tick(double dt, double now)
    {
        double t = now - _start;
        var pose = _pose.Clone();
        pose.LookX = 0.5;                                                    // schaut nach vorn
        Point center;
        double line, fade = 1;
        if (t < InMs)                                                        // hereinfahren bzw. abspringen, Linie zieht sich auf
        {
            double k = _drivesIn ? Out(t / InMs) : InOut(t / InMs);
            center = new Point(Lerp(_from.X, _ride.X, k), Lerp(_from.Y, _ride.Y, k) - (_drivesIn ? 0 : Math.Sin(Math.PI * k) * _hh * 0.35));
            pose.LookX = Lerp(_pose.LookX, 0.5, k);
            pose.Sx *= 1 + 0.08 * Math.Sin(Math.PI * k);
            pose.Sy *= 1 - 0.05 * Math.Sin(Math.PI * k);
            line = Out(t / InMs);
            _scroll += dt * _area.Width * 0.3 * k;
        }
        else if (t < InMs + RideMs)                                          // fahren: wippt im Tritt, Steinchen ziehen vorbei
        {
            double rt = (t - InMs) / 1000;
            double bob = Math.Sin(rt * 2 * Math.PI * 3);
            center = new Point(_ride.X, _ride.Y + bob * _hh * 0.03);
            pose.Sy *= 1 + bob * 0.025;
            pose.Sx *= 1 - bob * 0.015;
            line = 1;
            _scroll += dt * _area.Width * 0.55;
        }
        else                                                                 // landen: Übersicht erscheint, Linie verschwindet
        {
            if (!_landing)
            {
                _landing = true;
                Landing?.Invoke();                                           // Übersicht sichtbar machen und Layout rechnen
                _target!.UpdateLayout();
                var (c, hh) = _target.Body;
                _home = (_target.TranslatePoint(c, this), hh, _target.CurrentPose().Clone());
            }
            var (home, homeHh, homePose) = _home!.Value;
            double k = InOut(Seg(t - InMs - RideMs, 0, LandMs));
            center = new Point(Lerp(_ride.X, home.X, k), Lerp(_ride.Y, home.Y, k) - Math.Sin(Math.PI * k) * _hh * 0.35);
            pose = AvatarPose.Lerp(pose, homePose, k);
            pose.Sx *= 1 + 0.1 * Math.Sin(Math.PI * k);
            pose.Sy *= 1 - 0.06 * Math.Sin(Math.PI * k);
            line = 1;
            fade = 1 - Seg(k, 0, 0.6);
            _scroll += dt * _area.Width * 0.55 * (1 - k);
            if (k >= 1)
            {
                Running = false;
                Visibility = Visibility.Collapsed;
                _target!.Opacity = 1;
                if (_source is not null) _source.Opacity = 1;
                var done = _done;
                _done = null;
                done?.Invoke();
                return false;
            }
        }
        _frame = (center, pose, line, fade);
        InvalidateVisual();
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Running || _frame.Pose is null) return;
        var (center, pose, line, fade) = _frame;
        var white = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        double left = _area.Left + 18, right = _area.Right - 18, length = right - left;
        dc.PushClip(new RectangleGeometry(_area, 15, 15));                    // fährt hinter dem Rand herein
        if (fade > 0.01)
        {
            dc.PushOpacity(fade);
            var road = new Pen(white, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(road, new Point(left, _lineY), new Point(left + length * line, _lineY));
            var stone = new Pen(white, 1.5) { LineJoin = PenLineJoin.Round };
            foreach (var (u, w, h) in Stones)
            {
                double x = Wrap(left + u * length - _scroll, left, length);
                if (x - w / 2 < left || x + w / 2 > left + length * line) continue;
                var g = new StreamGeometry();
                using (var c = g.Open())                                     // Kuppe, die auf der Linie liegt
                {
                    c.BeginFigure(new Point(x - w / 2, _lineY), false, false);
                    c.BezierTo(new Point(x - w / 2, _lineY - h * 1.3), new Point(x + w / 2, _lineY - h * 1.3), new Point(x + w / 2, _lineY), true, true);
                }
                dc.DrawGeometry(null, stone, g);
            }
            foreach (var (u, dy) in Pebbles)
            {
                double x = Wrap(left + u * length - _scroll * 1.15, left, length);   // etwas schneller: liegen näher
                if (x > left + length * line) continue;
                dc.DrawEllipse(white, null, new Point(x, _lineY + dy), 0.9, 0.9);
            }
            dc.Pop();
        }
        AvatarArt.Draw(dc, pose, center, _hh, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.Pop();
    }

    private static double Wrap(double x, double left, double length) => left + ((x - left) % length + length) % length;
}
