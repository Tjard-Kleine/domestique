using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Domestique.App;

// Der Begleiter „Domestique“. Zeigt den Verbindungszustand (sucht, schläft ohne Daten, benommen bei Fehlern) und im
// Training die Anstrengung nach FTP-Zone (locker, konzentriert, am Limit, Farbe der Zone steigt von unten auf).
// Gezeichnet im Code (AvatarArt). Bewegt wird über animierbare Eigenschaften: Er zeichnet nur neu, solange sich etwas
// bewegt. In Ruhe kostet er nichts; Blinzeln sind ein paar Bilder alle paar Sekunden, nur solange er sichtbar ist.
// Abläufe und Zeiten (Blinzeln, Begrüßen, Klaps, Schwindel, Herzchen) folgen Coucou (MIT, siehe THIRD-PARTY-NOTICES.md).
public sealed class Avatar : FrameworkElement
{
    private static DependencyProperty Prop(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(Avatar), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OpenProperty = Prop(nameof(Open), 1);
    public static readonly DependencyProperty EyeScaleProperty = Prop(nameof(EyeScale), 1);
    public static readonly DependencyProperty LookXProperty = Prop(nameof(LookX), 0);
    public static readonly DependencyProperty LookYProperty = Prop(nameof(LookY), 0);
    public static readonly DependencyProperty SxProperty = Prop(nameof(Sx), 1);
    public static readonly DependencyProperty SyProperty = Prop(nameof(Sy), 1);
    public static readonly DependencyProperty OxProperty = Prop(nameof(Ox), 0);
    public static readonly DependencyProperty OyProperty = Prop(nameof(Oy), 0);
    public static readonly DependencyProperty TiltProperty = Prop(nameof(Tilt), 0);
    public static readonly DependencyProperty HandLProperty = Prop(nameof(HandL), 0);
    public static readonly DependencyProperty HandRProperty = Prop(nameof(HandR), 0);
    public static readonly DependencyProperty WaveProperty = Prop(nameof(Wave), -1);
    public static readonly DependencyProperty HaloProperty = Prop(nameof(Halo), 0.6);
    public static readonly DependencyProperty TintAmountProperty = Prop(nameof(TintAmount), 0);
    public static readonly DependencyProperty BadgeProperty = Prop(nameof(Badge), 0);
    public static readonly DependencyProperty RecProperty = Prop(nameof(Rec), 0);
    public static readonly DependencyProperty FxProperty = Prop(nameof(Fx), -1);
    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(nameof(TintColor), typeof(Color), typeof(Avatar),
        new FrameworkPropertyMetadata(Color.FromRgb(127, 180, 234), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HaloColorProperty = DependencyProperty.Register(nameof(HaloColor), typeof(Color), typeof(Avatar),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Open { get => (double)GetValue(OpenProperty); set => SetValue(OpenProperty, value); }
    public double EyeScale { get => (double)GetValue(EyeScaleProperty); set => SetValue(EyeScaleProperty, value); }
    public double LookX { get => (double)GetValue(LookXProperty); set => SetValue(LookXProperty, value); }
    public double LookY { get => (double)GetValue(LookYProperty); set => SetValue(LookYProperty, value); }
    public double Sx { get => (double)GetValue(SxProperty); set => SetValue(SxProperty, value); }
    public double Sy { get => (double)GetValue(SyProperty); set => SetValue(SyProperty, value); }
    public double Ox { get => (double)GetValue(OxProperty); set => SetValue(OxProperty, value); }
    public double Oy { get => (double)GetValue(OyProperty); set => SetValue(OyProperty, value); }
    public double Tilt { get => (double)GetValue(TiltProperty); set => SetValue(TiltProperty, value); }
    public double HandL { get => (double)GetValue(HandLProperty); set => SetValue(HandLProperty, value); }
    public double HandR { get => (double)GetValue(HandRProperty); set => SetValue(HandRProperty, value); }
    public double Wave { get => (double)GetValue(WaveProperty); set => SetValue(WaveProperty, value); }
    public double Halo { get => (double)GetValue(HaloProperty); set => SetValue(HaloProperty, value); }
    public double TintAmount { get => (double)GetValue(TintAmountProperty); set => SetValue(TintAmountProperty, value); }
    public double Badge { get => (double)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public double Rec { get => (double)GetValue(RecProperty); set => SetValue(RecProperty, value); }
    public double Fx { get => (double)GetValue(FxProperty); set => SetValue(FxProperty, value); }
    public Color TintColor { get => (Color)GetValue(TintColorProperty); set => SetValue(TintColorProperty, value); }
    public Color HaloColor { get => (Color)GetValue(HaloColorProperty); set => SetValue(HaloColorProperty, value); }

    private const double RestHalo = 0.6;                                     // leichter Schein in Ruhe
    private static readonly Random Rng = new();
    private readonly DispatcherTimer _blink = new(), _flashEnd = new(), _doneEnd = new(), _love = new();
    private readonly AvatarPose _pose = new();
    private Mood _mood = Mood.Search;
    private Mood? _flash;
    private BadgeKind _badgeShown;
    private bool _hover, _busy, _done, _recording;
    private double _lastLove = -10, _lookX, _lookY, _lookTau = 0.05;
    private FrameLoop? _lookLoop;

    public Avatar()
    {
        _blink.Tick += (_, _) => { _blink.Stop(); if (IsOpenFace) { Blink(); if (Rng.NextDouble() < 0.22) Motion.After(230, Blink); } ScheduleBlink(); };
        _flashEnd.Tick += (_, _) => { _flashEnd.Stop(); _flash = null; InvalidateVisual(); };
        _doneEnd.Tick += (_, _) => { _doneEnd.Stop(); _done = false; UpdateBadge(); };
        _love.Tick += (_, _) => { _love.Stop(); if (_hover) Love(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) ScheduleBlink(); else _blink.Stop(); };
        IsHitTestVisible = false;
    }

    // ---------- Zustand ----------

    // Grundstimmung. Kurze Ausdrücke (Flash) liegen vorübergehend darüber.
    public Mood Mood
    {
        get => _mood;
        set { if (_mood != value) { _mood = value; InvalidateVisual(); } }
    }

    public Mood Face => _flash ?? _mood;

    private bool IsOpenFace => Face is Mood.Idle or Mood.Search or Mood.Focus;

    // Aufnahme läuft: roter Punkt oben links. Beim Start springt er auf und der Avatar hüpft einmal.
    public bool Recording
    {
        set
        {
            if (_recording == value) return;
            _recording = value;
            Motion.Run(this, RecProperty, Rec, value ? 1 : 0, value ? 280 : 120, value ? Co.BackE : Co.InOutE);
            if (value) Hop();
        }
    }

    // Sucht gerade: blaues Abzeichen mit „…“
    public bool Busy
    {
        set { if (_busy != value) { _busy = value; UpdateBadge(); } }
    }

    // Geschafft: Abzeichen wird grün mit Haken und verschwindet nach ein paar Sekunden
    public void Done()
    {
        _done = true;
        UpdateBadge();
        _doneEnd.Stop();
        _doneEnd.Interval = TimeSpan.FromSeconds(6);
        _doneEnd.Start();
    }

    // Ein Ausdruck für kurze Zeit, danach wieder die Grundstimmung
    public void Flash(Mood face, int ms)
    {
        _flash = face;
        InvalidateVisual();
        _flashEnd.Stop();
        _flashEnd.Interval = TimeSpan.FromMilliseconds(ms);
        _flashEnd.Start();
    }

    // Farbe der Leistungszone steigt von unten auf, null = ganz weiß
    public void Tint(Color? color)
    {
        if (color is { } c)
        {
            if (Motion.Enabled) BeginAnimation(TintColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(350)) { EasingFunction = Co.InOutE });
            else { BeginAnimation(TintColorProperty, null); TintColor = c; }
        }
        double target = color is null ? 0 : 0.6;
        if (Math.Abs(TintAmount - target) > 0.01) Motion.Run(this, TintAmountProperty, TintAmount, target, 350, Co.InOutE);
    }

    // ---------- Bewegungen ----------

    public void Blink() => Motion.Keys(this, OpenProperty, 0, [(0, Open), (70, 0.06), (200, 1)]);

    // Begrüßen wie Coucou greet(): Freude, Hände kommen, 1,1 s winken mit leichtem Schwanken, Hände wieder weg
    public void Greet(int delayMs = 0)
    {
        Motion.After(delayMs, () =>
        {
            Flash(Mood.Happy, 2000);
            Motion.Keys(this, HaloProperty, 0, [(0, Halo), (280, 1), (1800, 1), (2300, RestHalo)]);
            Motion.Keys(this, OyProperty, 0, [(0, Oy), (220, -0.12), (440, 0)], Co.BackE);
            Motion.After(250, () =>
            {
                Motion.Run(this, HandLProperty, HandL, 1, 280, Co.OutE);
                Motion.Run(this, HandRProperty, HandR, 1, 280, Co.OutE);
                Motion.Keys(this, SyProperty, 0, [(0, 1), (100, 0.95), (360, 1)], Co.BackE);
                Motion.Keys(this, SxProperty, 0, [(0, 1), (100, 1.04), (360, 1)], Co.BackE);
            });
            Motion.Run(this, WaveProperty, 0, 1.1, 1100, Motion.Linear, 450, () => Wave = -1);
            var sway = new List<(int, double)>();
            for (int ms = 0; ms <= 1100; ms += 50) sway.Add((ms, -0.06 + Math.Sin(2 * Math.PI * 1.2 * ms / 1000.0) * 0.07));
            sway.Add((1300, 0));
            Motion.Keys(this, TiltProperty, 450, sway.ToArray());
            Motion.After(550, Blink);
            Motion.After(1500, Blink);
            Motion.After(1550, () =>
            {
                Motion.Run(this, HandLProperty, HandL, 0, 200, Co.InOutE);
                Motion.Run(this, HandRProperty, HandR, 0, 200, Co.InOutE);
            });
            Motion.After(1750, () => Flash(Mood.Happy, 300));
        });
    }

    // Kurzer Sprung mit Stauchen bei der Landung
    public void Hop(int delayMs = 0)
    {
        Motion.Keys(this, OyProperty, delayMs, [(0, 0), (150, -0.3), (300, 0)]);
        Motion.Keys(this, SyProperty, delayMs + 300, [(0, 1), (70, 0.84), (200, 1.08), (370, 1)]);
        Motion.Keys(this, SxProperty, delayMs + 300, [(0, 1), (70, 1.12), (200, 0.96), (370, 1)]);
    }

    // Klaps wie in Coucou: staucht sich, federt nach, kneift kurz die Augen zusammen
    public void Slap()
    {
        Motion.Keys(this, SyProperty, 0, [(0, Sy), (70, 0.78), (200, 1.1), (370, 1)]);
        Motion.Keys(this, SxProperty, 0, [(0, Sx), (70, 1.16), (200, 0.95), (370, 1)]);
        Motion.Keys(this, OxProperty, 0, [(0, 0), (90, Rng.NextDouble() < 0.5 ? 0.1 : -0.1), (400, 0)]);
        Flash(Mood.Squint, 800);
    }

    // Benommen: dreht sich zweimal um sich selbst (Coucou: 1,3 s)
    public void Roll(int turns = 2, int ms = 1300) =>
        Motion.Run(this, TiltProperty, 0, Math.PI * 2 * turns, ms, Co.InOutE, done: () => Tilt = 0);

    // Kurzes Kopfschütteln wie bei einem Fehler
    public void Shake(int delayMs = 0) =>
        Motion.Keys(this, OxProperty, delayMs, [(0, 0), (50, 0.16), (120, -0.16), (190, 0.1), (280, 0)]);

    // Maus darüber: blinzelt, die Augen werden etwas größer, der Schein etwas heller. Bleibt die Maus 1,9 s, gibt es Herzchen.
    public void Hover(bool on)
    {
        _hover = on;
        _love.Stop();
        Motion.Run(this, EyeScaleProperty, EyeScale, on ? 1.08 : 1, 200, Co.OutE);
        Motion.Run(this, HaloProperty, Halo, on ? 1 : RestHalo, on ? 220 : 400, Co.OutE);
        if (!on) return;
        if (IsOpenFace) Blink();
        if (Environment.TickCount64 / 1000.0 - _lastLove < 6) return;
        _love.Interval = TimeSpan.FromMilliseconds(1900);
        _love.Start();
    }

    // Herzaugen und aufsteigende Herzchen (1,8 s)
    public void Love()
    {
        _lastLove = Environment.TickCount64 / 1000.0;
        Flash(Mood.Love, 1800);
        Motion.Run(this, FxProperty, 0, 1.9, 1900, Motion.Linear, done: () => Fx = -1);
    }

    // Augen folgen der Maus (Abstand weich begrenzt). Ein Bild-für-Bild-Nachführen mit kurzer Zeitkonstante statt
    // einer neuen Animation bei jeder Mausbewegung: folgt ohne Verzögerung und hält an, sobald die Augen da sind.
    public void LookAt(Point p)
    {
        double hh = AvatarArt.HalfHeight(ActualWidth, ActualHeight), hw = hh * AvatarArt.Aspect;
        var c = AvatarArt.Center(ActualWidth, ActualHeight);
        Follow(Math.Tanh((p.X - c.X) / (hw * 3)), Math.Tanh((p.Y - c.Y) / (hh * 4)), 0.045);
    }

    public void LookAhead() => Follow(0, 0, 0.18);

    private void Follow(double x, double y, double tau)
    {
        _lookX = x;
        _lookY = y;
        _lookTau = tau;
        if (!Motion.Enabled) { LookX = x; LookY = y; return; }
        _lookLoop ??= new FrameLoop((dt, _) =>
        {
            double k = 1 - Math.Exp(-dt / _lookTau);
            LookX += (_lookX - LookX) * k;
            LookY += (_lookY - LookY) * k;
            if (Math.Abs(_lookX - LookX) > 0.003 || Math.Abs(_lookY - LookY) > 0.003) return true;
            LookX = _lookX;
            LookY = _lookY;
            return false;
        });
        _lookLoop.Start();
    }

    // ---------- Zeichnen ----------

    // Aktuelles Bild, z. B. als Ziel für den Start-Avatar, der hier landet
    internal AvatarPose CurrentPose()
    {
        var p = _pose;
        p.Face = Face; p.Open = Open; p.EyeScale = EyeScale; p.LookX = LookX; p.LookY = LookY;
        p.Sx = Sx; p.Sy = Sy; p.Ox = Ox; p.Oy = Oy; p.Tilt = Tilt;
        p.HandL = HandL; p.HandR = HandR; p.Wave = Wave;
        p.Halo = Halo; p.HaloColor = HaloColor; p.Tint = TintAmount; p.TintColor = TintColor;
        p.Badge = Badge; p.BadgeKind = _badgeShown; p.Rec = Rec; p.Fx = Fx;
        return p;
    }

    // Körpermitte und halbe Körperhöhe in den Koordinaten dieses Elements
    internal (Point Center, double HalfHeight) Body => (AvatarArt.Center(ActualWidth, ActualHeight), AvatarArt.HalfHeight(ActualWidth, ActualHeight));

    protected override void OnRender(DrawingContext dc)
    {
        var (center, hh) = Body;
        AvatarArt.Draw(dc, CurrentPose(), center, hh, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    // Nur der Körper reagiert auf die Maus, nicht der Schein
    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
    {
        var (c, hh) = Body;
        double hw = hh * AvatarArt.Aspect * 1.15;
        return Math.Abs(p.HitPoint.X - c.X) <= hw && Math.Abs(p.HitPoint.Y - c.Y) <= hh * 1.15 ? new PointHitTestResult(this, p.HitPoint) : null;
    }

    // ---------- intern ----------

    // Abzeichen wie in Coucou setBadge: kurz weg (90 ms), dann mit Nachfedern das neue (280 ms)
    private void UpdateBadge()
    {
        var kind = _done ? BadgeKind.Done : _busy ? BadgeKind.Busy : BadgeKind.None;
        if (kind == _badgeShown) return;
        Motion.Run(this, BadgeProperty, Badge, 0, 90, Co.InOutE, done: () =>
        {
            _badgeShown = kind;
            if (kind != BadgeKind.None) Motion.Run(this, BadgeProperty, 0, 1, 280, Co.BackE, 10);
        });
    }

    // Coucou: alle 2,2–5,4 s, manchmal doppelt
    private void ScheduleBlink()
    {
        _blink.Interval = TimeSpan.FromSeconds(2.2 + Rng.NextDouble() * 3.2);
        _blink.Start();
    }
}
