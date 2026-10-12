// Teile dieser Datei sind portiert aus Coucou (https://github.com/Louis-CFM/coucou), windows/src/core/anim.ts.
// Copyright (c) 2026 Louis Raillé, MIT-Lizenz (siehe THIRD-PARTY-NOTICES.md).
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Domestique.App;

// Kurven wie in Coucou: kubisch, „back“ mit Nachfedern und die Schließkurve cubic-bezier(.45, 0, .2, 1)
internal static class Co
{
    public static double Out(double t) => 1 - Math.Pow(1 - t, 3);
    public static double In(double t) => t * t * t;
    public static double InOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static double Back(double t) { const double c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2); }
    public static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Seg(double t, double a, double b) => Clamp((t - a) / (b - a), 0, 1);

    public static double Close(double x) => Bezier(0.45, 0, 0.2, 1, x);

    private static double Bezier(double x1, double y1, double x2, double y2, double x)
    {
        static double C(double t, double a, double b) => (1 - t) * (1 - t) * 3 * t * a + 3 * (1 - t) * t * t * b + t * t * t;
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 12; i++)
        {
            if (C(t, x1, x2) < x) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return C(t, y1, y2);
    }

    // dieselben Kurven als WPF-Easing für Storyboards und Motion
    public static readonly IEasingFunction OutE = Make(new OutCurve());
    public static readonly IEasingFunction InOutE = Make(new InOutCurve());
    public static readonly IEasingFunction BackE = Make(new BackCurve());
    public static readonly IEasingFunction CloseE = Make(new CloseCurve());

    private static IEasingFunction Make(EasingFunctionBase e) { e.EasingMode = EasingMode.EaseIn; e.Freeze(); return e; }

    private sealed class OutCurve : EasingFunctionBase { protected override double EaseInCore(double t) => Out(t); protected override System.Windows.Freezable CreateInstanceCore() => new OutCurve(); }
    private sealed class InOutCurve : EasingFunctionBase { protected override double EaseInCore(double t) => InOut(t); protected override System.Windows.Freezable CreateInstanceCore() => new InOutCurve(); }
    private sealed class BackCurve : EasingFunctionBase { protected override double EaseInCore(double t) => Back(t); protected override System.Windows.Freezable CreateInstanceCore() => new BackCurve(); }
    private sealed class CloseCurve : EasingFunctionBase { protected override double EaseInCore(double t) => Close(t); protected override System.Windows.Freezable CreateInstanceCore() => new CloseCurve(); }
}

// Feder wie SwiftUI .spring(response, dampingFraction): ω₀ = 2π / response, ζ = Dämpfung. Pro Bild in kleinen Schritten
// gerechnet, damit ein verlorenes Bild sie nie aus dem Tritt bringt.
internal sealed class Spring(double value, double response = 0.5, double damping = 0.72)
{
    public double Value = value, Target = value, Velocity;
    private double _omega = 2 * Math.PI / response, _zeta = damping;

    public void Configure(double response, double damping) { _omega = 2 * Math.PI / response; _zeta = damping; }
    public void Set(double v) { Value = Target = v; Velocity = 0; }
    public bool Settled => Math.Abs(Target - Value) < 0.01 && Math.Abs(Velocity) < 0.05;

    public void Step(double dt)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
        double h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            double acc = _omega * _omega * (Target - Value) - 2 * _zeta * _omega * Velocity;
            Velocity += acc * h;
            Value += Velocity * h;
        }
    }
}

// Wert, der beim Wachsen federt und beim Schrumpfen der Schließkurve folgt (340 ms, ohne Überschwingen)
internal sealed class Tracked(double value)
{
    private readonly Spring _spring = new(value);
    private double _from, _to, _start, _duration;
    private int _mode;                                                      // 0 ruhig, 1 Feder, 2 Kurve

    public double Value => _spring.Value;
    public double TargetValue => _spring.Target;
    public bool Animating => _mode != 0;

    public void Jump(double v) { _spring.Set(v); _mode = 0; }

    // Ziel unterwegs nachführen (z. B. wenn sich die Größe des Panels beim Aufgehen noch ändert)
    public void Retarget(double v) { if (_mode == 1) _spring.Target = v; }

    public void SpringTo(double v, double response = 0.5, double damping = 0.72)
    {
        _spring.Configure(response, damping);
        _spring.Target = v;
        _mode = 1;
    }

    public void CurveTo(double v, double durationMs, double now)
    {
        _from = _spring.Value; _to = v; _start = now; _duration = durationMs;
        _spring.Target = v; _spring.Velocity = 0;
        _mode = 2;
    }

    public void Step(double dt, double now)
    {
        if (_mode == 1)
        {
            _spring.Step(dt);
            if (_spring.Settled) { _spring.Set(_spring.Target); _mode = 0; }
        }
        else if (_mode == 2)
        {
            double p = Co.Clamp((now - _start) / _duration, 0, 1);
            _spring.Value = Co.Lerp(_from, _to, Co.Close(p));
            if (p >= 1) { _spring.Velocity = 0; _mode = 0; }
        }
    }
}

// Einmaliger Verlauf 0 → 1 über eine Kurve, für Werte, die WPF nicht selbst animieren kann (z. B. CornerRadius)
internal static class Tween
{
    public static void Run(int ms, Func<double, double> ease, Action<double> apply, Action? done = null)
    {
        if (!Motion.Enabled || ms <= 0) { apply(1); done?.Invoke(); return; }
        double start = double.NaN;
        FrameLoop? loop = null;
        loop = new FrameLoop((_, now) =>
        {
            if (double.IsNaN(start)) start = now;
            double p = Co.Clamp((now - start) / ms, 0, 1);
            apply(ease(p));
            if (p < 1) return true;
            done?.Invoke();
            return false;
        });
        apply(0);
        loop.Start();
    }
}

// Bild-für-Bild-Schleife über CompositionTarget.Rendering, nur solange jemand sie braucht. In Ruhe: keine Last.
internal sealed class FrameLoop
{
    private readonly Func<double, double, bool> _tick;                       // (dt in s, jetzt in ms) → weiterlaufen?
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _last;
    private bool _running;

    public FrameLoop(Func<double, double, bool> tick) => _tick = tick;

    public double Now => _clock.Elapsed.TotalMilliseconds;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _last = Now;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = Now, dt = Math.Min(0.05, (now - _last) / 1000);
        _last = now;
        if (!_tick(dt, now)) Stop();
    }
}
