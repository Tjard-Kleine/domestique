using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Domestique.App;

// Content: zufrieden mit geschlossenen Augen (beim Winken). Squint: genießt das Darüberfahren mit der Maus.
public enum Mood { Idle, Search, Sleep, Happy, Dizzy, Focus, Strain, Content, Squint }

// Der Avatar hat zwei Aufgaben: Er zeigt den Verbindungszustand (sucht, schläft ohne Daten, benommen bei Fehlern)
// und im Training die Anstrengung nach Leistungszone (locker, konzentriert, am Limit).
// Kosten: keine Dauer-Animation. Blinzeln sind zwei Neuzeichnungen alle paar Sekunden, nur solange er sichtbar ist.
// Begrüßen, Winken, Stupsen und Hüpfen sind kurze Animationen bei Ereignissen (Sheets in docs/sheets).
public partial class Avatar : UserControl
{
    private static readonly Random Rng = new();
    private readonly DispatcherTimer _blink = new(), _flashEnd = new(), _doneEnd = new();
    private Mood _mood = (Mood)(-1), _shown = (Mood)(-1);
    private Mood? _flash;
    private bool _eyesShut, _hover, _busy, _done, _recording;

    public Avatar()
    {
        InitializeComponent();
        _blink.Tick += (_, _) => Blink();
        _flashEnd.Tick += (_, _) => { _flashEnd.Stop(); _flash = null; ShowFace(); };
        _doneEnd.Tick += (_, _) => { _doneEnd.Stop(); _done = false; UpdateBadge(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) ScheduleBlink(); else _blink.Stop(); };
        Mood = Mood.Search;
    }

    // Grundstimmung. Kurze Ausdrücke (Flash) und das Darüberfahren liegen vorübergehend darüber.
    public Mood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            ShowFace();
        }
    }

    // Aufnahme läuft: roter Punkt. Beim Start springt er auf und der Avatar hüpft einmal.
    public bool Recording
    {
        set
        {
            if (_recording == value) return;
            _recording = value;
            UpdateBadge();
            if (value) Hop();
        }
    }

    // Sucht gerade: blaues Abzeichen mit „…“ (Sheet 2)
    public bool Busy
    {
        set
        {
            if (_busy == value) return;
            _busy = value;
            UpdateBadge();
        }
    }

    // Geschafft: Abzeichen wird grün mit Haken und verschwindet nach ein paar Sekunden (Sheet 6)
    public void Done()
    {
        _done = true;
        UpdateBadge();
        _doneEnd.Interval = TimeSpan.FromSeconds(6);
        _doneEnd.Stop();
        _doneEnd.Start();
    }

    // Ein Ausdruck für kurze Zeit, danach wieder die Grundstimmung
    public void Flash(Mood face, int ms)
    {
        _flash = face;
        ShowFace();
        _flashEnd.Interval = TimeSpan.FromMilliseconds(ms);
        _flashEnd.Stop();
        _flashEnd.Start();
    }

    // ---------- Bewegungen ----------

    // Begrüßen wie in Sheet 1: Schein, fröhliche Augen, dann winken
    public void Greet(int delayMs = 0)
    {
        Glow.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Motion.Keys(Glow, OpacityProperty, delayMs, [(0, Glow.Opacity), (280, 0.9), (2400, 0.9), (2900, 0)]);
        Motion.After(delayMs, () => Flash(Mood.Happy, 750));
        Wave(delayMs + 700);
    }

    // Winken wie in Sheet 1 und 2: Arme erscheinen, der rechte schwingt, die Figur schwankt leicht mit
    public void Wave(int delayMs = 0, int times = 3)
    {
        const int swing = 190;
        int end = swing * times * 2;
        var arm = new List<(int, double)> { (0, -30) };
        var sway = new List<(int, double)> { (0, 0) };
        var bob = new List<(int, double)> { (0, 37) };
        for (int i = 1; i <= times * 2; i++)
        {
            arm.Add((i * swing, i % 2 == 1 ? -72 : -18));
            sway.Add((i * swing, i % 2 == 1 ? -1.2 : 1.2));
            bob.Add((i * swing, i % 2 == 1 ? 35.5 : 37));
        }
        arm.Add((end + 200, -30));
        sway.Add((end + 200, 0));
        Motion.Keys(Arms, OpacityProperty, delayMs, [(0, 0), (140, 1), (end, 1), (end + 200, 0)]);
        Motion.Keys(ArmAngle, RotateTransform.AngleProperty, delayMs, arm.ToArray());
        Motion.Keys(FigureShift, TranslateTransform.XProperty, delayMs, sway.ToArray());
        Motion.Keys(LeftArm, Canvas.TopProperty, delayMs, bob.ToArray());
        Motion.After(delayMs + 150, () => Flash(Mood.Content, end - 150));    // genießt es mit geschlossenen Augen (Sheet 2)
    }

    // Kurzer Sprung mit Stauchen bei der Landung
    public void Hop(int delayMs = 0)
    {
        Motion.Keys(FigureShift, TranslateTransform.YProperty, delayMs, [(0, 0), (150, -8), (300, 0)]);
        var (x, y) = RestScale;
        Motion.Keys(FigureScale, ScaleTransform.ScaleXProperty, delayMs + 300, [(0, x), (70, x * 1.12), (440, x)], Motion.Pop);
        Motion.Keys(FigureScale, ScaleTransform.ScaleYProperty, delayMs + 300, [(0, y), (70, y * 0.86), (440, y)], Motion.Pop);
    }

    // Antippen: staucht sich zusammen und federt zurück (Sheet 9)
    public void Poke()
    {
        var (x, y) = RestScale;
        Motion.Keys(FigureScale, ScaleTransform.ScaleXProperty, 0, [(0, FigureScale.ScaleX), (70, x * 1.14), (460, x)], Motion.Pop);
        Motion.Keys(FigureScale, ScaleTransform.ScaleYProperty, 0, [(0, FigureScale.ScaleY), (70, y * 0.84), (460, y)], Motion.Pop);
    }

    // Benommen: kurzes Kopfschütteln
    public void Shake(int delayMs = 0) =>
        Motion.Keys(FigureShift, TranslateTransform.XProperty, delayMs, [(0, 0), (60, -3), (130, 3), (200, -2.4), (270, 1.8), (340, -1), (420, 0)]);

    // Maus darüber: Augen werden schmal, die Figur wird etwas breiter, darunter leuchtet es (Sheet 8)
    public void Hover(bool on)
    {
        _hover = on;
        ShowFace();
        if (on) Glow.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Motion.Run(Glow, OpacityProperty, Glow.Opacity, on ? 1 : 0, on ? 220 : 320);
        var (x, y) = RestScale;
        Motion.Run(FigureScale, ScaleTransform.ScaleXProperty, FigureScale.ScaleX, x, on ? 180 : 420, on ? Motion.Out : Motion.Pop);
        Motion.Run(FigureScale, ScaleTransform.ScaleYProperty, FigureScale.ScaleY, y, on ? 180 : 420, on ? Motion.Out : Motion.Pop);
    }

    // Farbton der Leistungszone, null = eigene Farbe
    public void Tint(Color? color)
    {
        if (color is { } c)
        {
            if (Motion.Enabled) TintBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(c, TimeSpan.FromMilliseconds(350)) { EasingFunction = Motion.Out });
            else { TintBrush.BeginAnimation(SolidColorBrush.ColorProperty, null); TintBrush.Color = c; }
        }
        double target = color is null ? 0 : 0.7;                    // pastellig: etwas Weiß bleibt
        if (Math.Abs(ZoneTint.Opacity - target) > 0.01) Motion.Run(ZoneTint, OpacityProperty, ZoneTint.Opacity, target, 350);
    }

    private (double X, double Y) RestScale => _hover ? (1.07, 0.93) : (1, 1);

    // ---------- Gesicht ----------

    private void ShowFace()
    {
        var face = _flash ?? (_hover ? Mood.Squint : _mood);
        if (face == _shown) return;
        _shown = face;
        bool open = face is Mood.Idle or Mood.Search or Mood.Focus;
        EyesOpen.Visibility = Show(open);
        Brows.Visibility = Show(face == Mood.Focus);
        EyesClosed.Visibility = Show(face is Mood.Sleep or Mood.Content);
        Zzz.Visibility = Show(face == Mood.Sleep);
        EyesHappy.Visibility = Show(face == Mood.Happy);
        EyesSquint.Visibility = Show(face == Mood.Squint);
        EyesDizzy.Visibility = Show(face == Mood.Dizzy);
        EyesStrain.Visibility = Sweat.Visibility = Show(face == Mood.Strain);
        Cheeks1.Visibility = Cheeks2.Visibility = Show(face is Mood.Happy or Mood.Dizzy or Mood.Strain or Mood.Content or Mood.Squint);
        EyeShift.X = face == Mood.Search ? 3.5 : 0;                // schaut sich um
        EyeScale.ScaleY = OpenScale;
        _eyesShut = false;
    }

    private double OpenScale => _shown == Mood.Focus ? 0.75 : 1;

    private void Blink()
    {
        if (EyesOpen.Visibility != Visibility.Visible) { ScheduleBlink(); return; }
        _eyesShut = !_eyesShut;
        EyeScale.ScaleY = _eyesShut ? 0.1 : OpenScale;
        if (_eyesShut) _blink.Interval = TimeSpan.FromMilliseconds(130);
        else ScheduleBlink();
    }

    private void ScheduleBlink()
    {
        _blink.Interval = TimeSpan.FromSeconds(3 + Rng.NextDouble() * 4);
        _blink.Start();
    }

    // ---------- Abzeichen ----------

    // Suchen oder geschafft oben links; der Aufnahmepunkt sitzt an derselben Stelle und weicht solange
    private void UpdateBadge()
    {
        bool badge = _busy || _done;
        if (badge)
        {
            bool changed = (BadgeDone.Visibility == Visibility.Visible) != _done;
            BadgeDot.SetResourceReference(Shape.FillProperty, _done ? "PositiveBrush" : "AccentBrush");
            BadgeBusy.Visibility = Show(!_done);
            BadgeDone.Visibility = Show(_done);
            if (changed && Badge.Tag is true) Motion.Bounce(Badge, 0.7, 420);   // färbt sich um und springt kurz
        }
        Toggle(Badge, badge);
        Toggle(RecDot, _recording && !badge);
    }

    // erscheinen mit kleinem Sprung, verschwinden geschrumpft. Tag hält den gewünschten Zustand, falls er sich mittendrin ändert.
    private static void Toggle(FrameworkElement element, bool on)
    {
        if (on == (element.Tag is true)) return;
        element.Tag = on;
        if (on)
        {
            element.Visibility = Visibility.Visible;
            Motion.PopIn(element, 0.3, 380);
        }
        else Motion.PopOut(element, 0.3, 140, () => { if (element.Tag is false) element.Visibility = Visibility.Collapsed; });
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
