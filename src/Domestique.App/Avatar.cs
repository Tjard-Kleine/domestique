using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Domestique.App;

// The companion. Shows the connection state (searching, asleep without devices, warning badge) and, while riding,
// the effort by FTP zone (relaxed, focused, at the limit; the zone color rises from the bottom). Drawn in code
// (AvatarArt) and moved through animatable properties, so it only redraws while something moves.
// Timings of blinking, greeting, slaps, dizziness and hearts follow Coucou (MIT, see THIRD-PARTY-NOTICES.md).
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
    public double Fx { get => (double)GetValue(FxProperty); set => SetValue(FxProperty, value); }
    public Color TintColor { get => (Color)GetValue(TintColorProperty); set => SetValue(TintColorProperty, value); }
    public Color HaloColor { get => (Color)GetValue(HaloColorProperty); set => SetValue(HaloColorProperty, value); }

    private const double RestHalo = 0.6;                                     // soft glow at rest
    private static readonly Random Rng = new();
    private readonly DispatcherTimer _blink = new(), _flashEnd = new(), _doneEnd = new(), _love = new();
    private readonly AvatarPose _pose = new();
    private Mood _mood = Mood.Search;
    private Mood? _flash;
    private BadgeKind _badgeShown;
    private bool _hover, _busy, _done, _warning, _gear;
    private Color? _zoneColor;
    private double _tintTarget;
    private static readonly Color SleepGray = Color.FromRgb(150, 156, 168);   // gray version of the launch blue
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

    // == State ==

    // Base mood; short expressions (Flash) temporarily override it.
    public Mood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            bool woke = _mood == Mood.Sleep && value != Mood.Sleep;
            bool sleeps = value == Mood.Sleep && _mood != Mood.Sleep;
            _mood = value;
            InvalidateVisual();
            if (woke || sleeps) UpdateTint();
            if (woke && IsLoaded) WakeUp();
        }
    }

    public Mood Face => _flash ?? _mood;

    private bool IsOpenFace => Face is Mood.Idle or Mood.Search or Mood.Focus;

    // Searching: blue badge with "…"
    public bool Busy
    {
        set { if (_busy != value) { _busy = value; UpdateBadge(); } }
    }

    // Control lost: yellow "!"
    public bool Warning
    {
        set { if (_warning != value) { _warning = value; UpdateBadge(); } }
    }

    // Settings: gray gear badge, permanent
    public bool Gear
    {
        set { if (_gear != value) { _gear = value; UpdateBadge(); } }
    }

    // Connected or loaded: green check badge that disappears after a few seconds.
    public void Done()
    {
        _done = true;
        UpdateBadge();
        _doneEnd.Stop();
        _doneEnd.Interval = TimeSpan.FromSeconds(6);
        _doneEnd.Start();
    }

    // An expression for a short time, then back to the base mood.
    public void Flash(Mood face, int ms)
    {
        _flash = face;
        InvalidateVisual();
        _flashEnd.Stop();
        _flashEnd.Interval = TimeSpan.FromMilliseconds(ms);
        _flashEnd.Start();
    }

    // Zone color rises from the bottom; null = white. Gray while asleep.
    public void Tint(Color? color)
    {
        _zoneColor = color;
        UpdateTint();
    }

    private void UpdateTint()
    {
        Color? color = _mood == Mood.Sleep ? SleepGray : _zoneColor;
        if (color is { } c)
        {
            if (Motion.Enabled) BeginAnimation(TintColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(450)) { EasingFunction = Co.InOutE });
            else { BeginAnimation(TintColorProperty, null); TintColor = c; }
        }
        // compare with the last target, not the value: it may match by chance in the middle of another fade
        double target = color is null ? 0 : 0.6;
        if (Math.Abs(_tintTarget - target) < 0.01) return;
        _tintTarget = target;
        Motion.Run(this, TintAmountProperty, TintAmount, target, 450, Co.InOutE);
    }

    // Wake up: stretches up briefly and blinks.
    private void WakeUp()
    {
        Motion.Keys(this, SyProperty, 0, [(0, Sy), (160, 1.12), (320, 0.94), (520, 1)], Co.BackE);
        Motion.Keys(this, SxProperty, 0, [(0, Sx), (160, 0.92), (320, 1.05), (520, 1)], Co.BackE);
        Motion.After(380, Blink);
    }

    // Too many slaps: spins dizzily.
    public void Dizzy()
    {
        Flash(Mood.Dizzy, 2200);
        Roll();
    }

    // == Moves ==

    public void Blink() => Motion.Keys(this, OpenProperty, 0, [(0, Open), (70, 0.06), (200, 1)]);

    // Greeting like Coucou greet(): joy, hands come out, waves for 1.1 s while swaying, hands go away.
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

    // Short hop, squashing on landing.
    public void Hop(int delayMs = 0)
    {
        Motion.Keys(this, OyProperty, delayMs, [(0, 0), (150, -0.3), (300, 0)]);
        Motion.Keys(this, SyProperty, delayMs + 300, [(0, 1), (70, 0.84), (200, 1.08), (370, 1)]);
        Motion.Keys(this, SxProperty, delayMs + 300, [(0, 1), (70, 1.12), (200, 0.96), (370, 1)]);
    }

    // Slap like in Coucou: squashes, springs back, squints.
    public void Slap()
    {
        Motion.Keys(this, SyProperty, 0, [(0, Sy), (70, 0.78), (200, 1.1), (370, 1)]);
        Motion.Keys(this, SxProperty, 0, [(0, Sx), (70, 1.16), (200, 0.95), (370, 1)]);
        Motion.Keys(this, OxProperty, 0, [(0, 0), (90, Rng.NextDouble() < 0.5 ? 0.1 : -0.1), (400, 0)]);
        Flash(Mood.Squint, 800);
    }

    // Dizzy: spins twice (Coucou: 1.3 s).
    public void Roll(int turns = 2, int ms = 1300) =>
        Motion.Run(this, TiltProperty, 0, Math.PI * 2 * turns, ms, Co.InOutE, done: () => Tilt = 0);

    // Mouse over: blinks, eyes grow a little, glow brightens. After 1.9 s hovering: hearts.
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

    // Heart eyes and rising hearts (1.8 s).
    public void Love()
    {
        _lastLove = Environment.TickCount64 / 1000.0;
        Flash(Mood.Love, 1800);
        Motion.Run(this, FxProperty, 0, 1.9, 1900, Motion.Linear, done: () => Fx = -1);
    }

    // Eyes follow the mouse (softly limited). A per-frame follow with a short time constant instead of a new animation
    // per mouse move: no lag, and it stops once the eyes arrive.
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

    // == Rendering ==

    // Current frame, e.g. as the landing target of the launch avatar.
    internal AvatarPose CurrentPose()
    {
        var p = _pose;
        p.Face = Face; p.Open = Open; p.EyeScale = EyeScale; p.LookX = LookX; p.LookY = LookY;
        p.Sx = Sx; p.Sy = Sy; p.Ox = Ox; p.Oy = Oy; p.Tilt = Tilt;
        p.HandL = HandL; p.HandR = HandR; p.Wave = Wave;
        p.Halo = Halo; p.HaloColor = HaloColor; p.Tint = TintAmount; p.TintColor = TintColor;
        p.Badge = Badge; p.BadgeKind = _badgeShown; p.Fx = Fx;
        return p;
    }

    // Body center and half body height in this element's coordinates.
    internal (Point Center, double HalfHeight) Body => (AvatarArt.Center(ActualWidth, ActualHeight), AvatarArt.HalfHeight(ActualWidth, ActualHeight));

    protected override void OnRender(DrawingContext dc)
    {
        var (center, hh) = Body;
        AvatarArt.Draw(dc, CurrentPose(), center, hh, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    // Only the body reacts to the mouse, not the glow.
    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
    {
        var (c, hh) = Body;
        double hw = hh * AvatarArt.Aspect * 1.15;
        return Math.Abs(p.HitPoint.X - c.X) <= hw && Math.Abs(p.HitPoint.Y - c.Y) <= hh * 1.15 ? new PointHitTestResult(this, p.HitPoint) : null;
    }

    // == Internals ==

    // Badge like Coucou setBadge: out briefly (90 ms), then the new one springs in (280 ms).
    private void UpdateBadge()
    {
        var kind = _done ? BadgeKind.Done : _warning ? BadgeKind.Warning : _busy ? BadgeKind.Busy : _gear ? BadgeKind.Settings : BadgeKind.None;
        if (kind == _badgeShown) return;
        Motion.Run(this, BadgeProperty, Badge, 0, 90, Co.InOutE, done: () =>
        {
            _badgeShown = kind;
            if (kind != BadgeKind.None) Motion.Run(this, BadgeProperty, 0, 1, 280, Co.BackE, 10);
        });
    }

    // Coucou: every 2.2–5.4 s, sometimes twice
    private void ScheduleBlink()
    {
        _blink.Interval = TimeSpan.FromSeconds(2.2 + Rng.NextDouble() * 3.2);
        _blink.Start();
    }
}
