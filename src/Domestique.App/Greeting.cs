// Ablauf, Zeiten, Kurven und Partikel portiert aus Coucou (https://github.com/Louis-CFM/coucou),
// windows/src/mochi/greeting.ts. Copyright (c) 2026 Louis Raillé, MIT-Lizenz (siehe THIRD-PARTY-NOTICES.md).
// Gezeichnet wird unser eigener Avatar (AvatarArt), nicht Coucous Figur.
using System.Windows;
using System.Windows.Media;
using static Domestique.App.Co;

namespace Domestique.App;

// Der Start: Aus der kleinen Pille fällt der Avatar ins aufgehende Overlay, Lichtstreifen und ein Funkenring,
// er landet, federt, freut sich, rutscht zur Seite und winkt, kommt zurück, blinzelt, das Abzeichen erscheint und
// Blau steigt in ihm auf. Danach gleitet er auf seinen Platz: in die Übersicht oder, wenn Bluetooth aus ist,
// nach links in die Bluetooth-Karte. Läuft nur beim Start, Bild für Bild, danach ist die Ansicht weg.
internal sealed class GreetingView : FrameworkElement
{
    // Zeitplan in Sekunden (Coucou: T)
    private const double Pop0 = 1.3, Pop1 = 1.45, Content0 = 2.4, Tuck0 = 2.45, Tuck1 = 2.7, BadgeAt = 2.72,
        Down0 = 2.85, Down1 = 3.45, Blink2 = 3.7, Tint0 = 3.85, Tint1 = 4.15, End = 4.6, LandMs = 560;
    private const double Hb = 58;                                          // Körperhöhe beim Start
    private const double SmallK = 0.29;                                    // Größe in der Pille, relativ zu Hb

    private static readonly (double X, double Speed, double Len, double Thick, double Alpha, double T0)[] Warps;
    private static readonly (double A, double J, double S, double Al)[] RingDots;

    static GreetingView()
    {
        // gleiche Folge wie Coucou: lineare Kongruenz mit Startwert 7
        uint seed = 7;
        double Rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / (double)0x7fffffff; }
        Warps = Enumerable.Range(0, 70).Select(_ => (Rnd(), 400 + Rnd() * 300, 6 + Rnd() * 16, 1 + Rnd() * 0.5, 0.25 + Rnd() * 0.55, Rnd() * 0.35)).ToArray();
        RingDots = Enumerable.Range(0, 90).Select(_ => (Rnd() * Math.PI * 2, (Rnd() - 0.5) * 0.22, 0.7 + Rnd() * 0.9, 0.45 + Rnd() * 0.55)).ToArray();
    }

    private readonly FrameLoop _loop;
    private Rect _card;
    private double _compactW, _compactH, _t, _landStart = double.NaN, _startMs, _landMs = LandMs;
    private bool _greeting;
    private Frame _frame, _landFrom;
    private (Point Center, double HalfHeight, AvatarPose Pose)? _target;
    private Action? _flightDone;

    public Func<Avatar>? ChooseTarget;                                     // Wohin er am Ende gleitet
    public Func<Rect>? IslandBounds;                                       // sichtbarer Teil der Insel, in eigenen Koordinaten
    public event Action<Avatar>? Landing;                                  // Beginn des Gleitens: Inhalt einblenden
    public event Action? Finished;
    public event Action<double>? HeaderOpacity;

    public bool Running { get; private set; }

    public GreetingView()
    {
        IsHitTestVisible = false;
        _loop = new FrameLoop(Tick);
    }

    // Die Ansicht muss sichtbar und angeordnet sein, bevor man Punkte in ihr misst
    public void Prepare()
    {
        Visibility = Visibility.Visible;
        UpdateLayout();
    }

    public void Start(Rect card, double compactW, double compactH)
    {
        _card = card;
        _compactW = compactW;
        _compactH = compactH;
        _landStart = double.NaN;
        _landMs = LandMs;
        _target = null;
        _greeting = true;
        _startMs = _loop.Now;
        Running = true;
        Visibility = Visibility.Visible;
        _frame = Pose(0);
        _loop.Start();
        InvalidateVisual();
    }

    // Klick während des Starts: gleich auf den Platz gleiten
    public void Skip()
    {
        if (Running && _greeting && double.IsNaN(_landStart)) BeginLand(_loop.Now);
    }

    // Avatar gleitet von einem Platz zum anderen (z. B. aus der Übersicht in die Bluetooth-Karte und zurück).
    // Beide echten Avatare sind währenddessen unsichtbar; am Ende erscheint der Ziel-Avatar genau dort.
    public void Fly(Avatar from, Avatar to, int ms = 560, Action? done = null)
    {
        if (Running) return;
        Prepare();
        to.UpdateLayout();
        var (c1, hh1) = from.Body;
        var (c2, hh2) = to.Body;
        var start = from.TranslatePoint(c1, this);
        _landFrom = new Frame { X = start.X, Y = start.Y, Hb = hh1 * 2, Pose = from.CurrentPose().Clone(), Header = 1 };
        _target = (to.TranslatePoint(c2, this), hh2, to.CurrentPose().Clone());
        from.Opacity = 0;
        to.Opacity = 0;
        _flightDone = () => { to.Opacity = 1; done?.Invoke(); };
        _greeting = false;
        _landMs = Motion.Enabled ? ms : 1;
        _landStart = _loop.Now;
        _frame = _landFrom;
        Running = true;
        _loop.Start();
        InvalidateVisual();
    }

    private bool Tick(double dt, double now)
    {
        _t = (now - _startMs) / 1000;
        if (_greeting && double.IsNaN(_landStart))
        {
            if (_t >= End) BeginLand(now);
            else _frame = Pose(_t);
        }
        if (!double.IsNaN(_landStart))
        {
            double k = InOut(Seg(now - _landStart, 0, _landMs));
            var (center, hh, pose) = _target!.Value;
            _frame = _landFrom;
            _frame.X = Lerp(_landFrom.X, center.X, k);
            _frame.Y = Lerp(_landFrom.Y, center.Y, k) - Math.Sin(Math.PI * k) * hh * (_greeting ? 0 : 0.35);   // kleiner Bogen
            _frame.Hb = Lerp(_landFrom.Hb, hh * 2, k);
            _frame.Pose = AvatarPose.Lerp(_landFrom.Pose, pose, k);
            if (!_greeting)                                                 // streckt sich in Flugrichtung
            {
                _frame.Pose.Sx *= 1 + 0.1 * Math.Sin(Math.PI * k);
                _frame.Pose.Sy *= 1 - 0.06 * Math.Sin(Math.PI * k);
            }
            _frame.Card = _landFrom.Card * (1 - Seg(k, 0, 0.6));
            _frame.Particles = false;
            if (k >= 1)
            {
                Running = false;
                Visibility = Visibility.Collapsed;
                if (_greeting) { _greeting = false; Finished?.Invoke(); }
                else { var done = _flightDone; _flightDone = null; done?.Invoke(); }
                return false;
            }
        }
        if (_greeting) HeaderOpacity?.Invoke(_frame.Header);
        InvalidateVisual();
        return true;
    }

    private void BeginLand(double now)
    {
        _landFrom = _frame;
        _landFrom.Header = 1;
        _landStart = now;
        var avatar = ChooseTarget?.Invoke();
        if (avatar is null) { _target = (new Point(_frame.X, _frame.Y), _frame.Hb / 2, _frame.Pose.Clone()); return; }
        Landing?.Invoke(avatar);                                            // Ziel sichtbar machen und Layout rechnen
        avatar.UpdateLayout();
        var (c, hh) = avatar.Body;
        _target = (avatar.TranslatePoint(c, this), hh, avatar.CurrentPose().Clone());
    }

    // ---------- Bild ----------

    private struct Frame
    {
        public AvatarPose Pose;
        public double X, Y, Hb, Header, Card, Iw;
        public bool Particles;
    }

    // Coucou greetPose: Fall, Landung, Federn, zur Seite rutschen, winken, Hände einziehen, zurück
    private Frame Pose(double t)
    {
        double w = ActualWidth;
        var c0 = new Point(w / 2, _card.Top + _card.Height / 2 + 2);
        double gx = Seg(t, 0, 0.5);
        double g = Math.Sin(Math.PI * gx / 2) + 0.04 * Math.Sin(Math.PI * gx) * gx;
        double pillY = _compactH / 2;

        double hb = t < 0.2 ? Hb * SmallK : Lerp(Hb * SmallK, Hb, Back(Seg(t, 0.2, 0.6)));
        double cx = c0.X, cy = c0.Y;
        double landY = cy + 0.12 * Hb, peakY = cy - 0.15 * Hb, dipY = cy + 0.36 * Hb, springY = cy - 0.1 * Hb, sinkY = cy + 0.3 * Hb;
        double d1 = cx - 0.16 * Hb, d2 = cx - 0.45 * Hb, d3 = cx - 0.57 * Hb, d4 = cx - 0.85 * Hb;

        double x = t < 0.85 ? cx
            : t < 1.2 ? Lerp(cx, d1, InOut(Seg(t, 0.85, 1.2)))
            : t < 1.3 ? Lerp(d1, d2, In(Seg(t, 1.2, 1.3)))
            : t < 1.45 ? Lerp(d2, d3, InOut(Seg(t, 1.3, 1.45)))
            : t < 2.4 ? Lerp(d3, d4, InOut(Seg(t, 1.45, 2.4)))
            : t < 2.85 ? d4
            : Lerp(d4, cx, InOut(Seg(t, 2.85, 3.45)));

        double y = t < 0.2 ? pillY
            : t < 0.6 ? Lerp(pillY, landY, In(Seg(t, 0.2, 0.6)))
            : t < 0.73 ? Lerp(landY, peakY, Out(Seg(t, 0.6, 0.73)))
            : t < 0.9 ? Lerp(peakY, cy, InOut(Seg(t, 0.73, 0.9)))
            : t < 1.2 ? cy
            : t < 1.3 ? Lerp(cy, dipY, In(Seg(t, 1.2, 1.3)))
            : t < 1.45 ? Lerp(dipY, springY, Out(Seg(t, 1.3, 1.45)))
            : t < 1.6 ? Lerp(springY, cy, InOut(Seg(t, 1.45, 1.6)))
            : t < 2.4 ? cy
            : t < 2.7 ? Lerp(cy, sinkY, InOut(Seg(t, 2.4, 2.7)))
            : t < 2.85 ? sinkY
            : Lerp(sinkY, cy, InOut(Seg(t, 2.85, 3.45)));
        if (t >= Pop1 && t < Tuck0)                                         // wippt im Takt der winkenden Hand
        {
            double wt = t - Pop1;
            y += Math.Sin(wt * 2 * Math.PI * 5) * 0.02 * Hb * Clamp(wt / 0.08, 0, 1);
        }

        double land = Bump(t, 0.52, 0.68), bounce = Bump(t, 0.62, 0.84), plunge = Bump(t, 1.18, 1.42), spring = Bump(t, 1.3, 1.46), micro = Bump(t, 3.7, 3.82);
        double sink = t >= 2.38 && t < 2.7 ? InOut(Seg(t, 2.38, 2.7)) : t >= 2.7 && t < 2.85 ? 1 - InOut(Seg(t, 2.7, 2.85)) : 0;
        double sx = 1 + 0.14 * land - 0.1 * bounce + 0.12 * plunge - 0.18 * spring + 0.18 * sink + 0.08 * micro;
        double sy = 1 - 0.14 * land + 0.18 * bounce - 0.12 * plunge + 0.25 * spring - 0.14 * sink - 0.07 * micro;

        var face = t >= 0.55 && t < 0.8 ? Mood.Happy : t >= Content0 && t < Tuck1 ? Mood.Content : Mood.Idle;
        double open = Math.Min(Blink(t, 1.95), Math.Min(Blink(t, 3.05), Blink(t, Blink2)));
        double lookX = 0, lookY = 0;
        if (t >= Pop1 && t < Content0) { lookX = 0.55; lookY = -0.45; }
        else if (t >= Content0 && t < Down0) { lookX = -0.3; lookY = 0.6; }
        else if (t >= Down0 && t < Down1) { lookX = 0.3; lookY = 0.6; }
        else if (t >= Down1) { double k = InOut(Seg(t, Down1, Down1 + 0.35)); lookX = Lerp(0.3, 0, k); lookY = Lerp(0.6, 0, k); }

        double handL = t < Tuck0 ? Back(Seg(t, Pop0, Pop0 + 0.14)) : 1 - In(Seg(t, Tuck0, Tuck1 - 0.03));
        double handR = t < Tuck0 ? Back(Seg(t, Pop0 + 0.04, Pop0 + 0.18)) : 1 - In(Seg(t, Tuck0 + 0.03, Tuck1));
        double blue = Seg(t, Tint0, Tint1);

        return new Frame
        {
            X = x, Y = y, Hb = hb, Iw = Lerp(_compactW, w, g),
            Header = Seg(t, 0.35, 0.6), Card = Seg(t, 0.18, 0.45), Particles = true,
            Pose = new AvatarPose
            {
                Face = face, Open = open, LookX = lookX, LookY = lookY, Sx = sx, Sy = sy,
                HandL = handL, HandR = handR, Wave = t >= Pop1 && t < Tuck0 ? t - Pop1 : -1,
                BadgeKind = BadgeKind.Busy, Badge = Back(Seg(t, BadgeAt, BadgeAt + 0.28)),
                Tint = 0.6 * InOut(Seg(t, Tint0, Tint1)), TintColor = Color.FromRgb(127, 180, 234),
                Halo = Out(Seg(t, 0.3, 0.7)), HaloSpread = 1,
                HaloColor = Color.FromRgb((byte)Lerp(232, 59, blue), (byte)Lerp(195, 158, blue), (byte)Lerp(154, 255, blue)),
            },
        };
    }

    private static double Bump(double t, double a, double b) => t >= a && t < b ? Math.Sin(Math.PI * Seg(t, a, b)) : 0;

    private static double Blink(double t, double at)
    {
        double k = Seg(t, at, at + 0.12);
        return k > 0 && k < 1 ? 1 - Math.Sin(Math.PI * k) * 0.94 : 1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Running || _frame.Pose is null) return;
        var f = _frame;
        double radius = 16;
        // Die Karte bleibt immer mit Abstand in der Insel, auch während die noch aufgeht
        var card = _card;
        if (IslandBounds?.Invoke() is { } island)
            card.Intersect(new Rect(island.Left + 10, island.Top, Math.Max(0, island.Width - 20), Math.Max(0, island.Height - 10)));
        if (f.Card > 0 && !card.IsEmpty && card.Width > 2 * radius)
        {
            dc.PushOpacity(f.Card);
            dc.DrawRoundedRectangle(TryFindResource("CardBrush") as Brush, null, card, radius, radius);
            dc.Pop();
            if (f.Particles)
            {
                dc.PushClip(new RectangleGeometry(card, radius, radius));
                DrawParticles(dc, _t, f);
                dc.Pop();
            }
        }
        AvatarArt.Draw(dc, f.Pose, new Point(f.X, f.Y), f.Hb / 2, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    // Lichtstreifen fallen beim Hereinfallen (0–0,55 s), ein Funkenring breitet sich ab 0,45 s aus
    private void DrawParticles(DrawingContext dc, double t, Frame f)
    {
        var brush = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        double w = ActualWidth, h = ActualHeight;
        if (t < 0.55)
        {
            double fade = 1 - Seg(t, 0.4, 0.55);
            foreach (var s in Warps)
            {
                if (t < s.T0) continue;
                double bottom = (t - s.T0) * s.Speed, top = bottom - s.Len;
                if (bottom <= 0) continue;
                double x = w / 2 - f.Iw / 2 + s.X * f.Iw;
                dc.PushOpacity(s.Alpha * fade);
                dc.DrawRectangle(brush, null, new Rect(x - s.Thick / 2, Math.Max(0, top), s.Thick, Math.Max(0, Math.Min(h, bottom) - Math.Max(0, top))));
                dc.Pop();
            }
        }
        double k = Seg(t, 0.45, 0.45 + 1.35);
        if (k > 0 && k < 1)
        {
            double rx = Lerp(14, w * 0.53, Out(k)), ry = rx * 0.34;
            double fade = (1 - k) * (k < 0.08 ? k / 0.08 : 1) * f.Card;
            var c0 = new Point(w / 2, _card.Top + _card.Height / 2 + 2);
            foreach (var d in RingDots)
            {
                double r = 1 + d.J;
                dc.PushOpacity(d.Al * fade);
                dc.DrawRectangle(brush, null, new Rect(c0.X + Math.Cos(d.A) * rx * r, c0.Y + Math.Sin(d.A) * ry * r, d.S, d.S));
                dc.Pop();
            }
        }
    }
}
