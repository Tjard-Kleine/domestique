using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Domestique.App;

// Mikroanimationen nach den Sheets in docs/sheets: Alles wächst etwas über das Ziel hinaus und schnappt leicht zurück.
// Nur kurze Animationen bei Ereignissen, keine Dauerschleifen: Zwischen den Animationen zeichnet WPF nichts neu.
internal static class Motion
{
    // In Windows „Animationseffekte“ ausgeschaltet: Alles springt sofort ans Ziel (auch für Vorschaubilder)
    public static bool Enabled { get; set; } = SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction Pop = Ease(new BackEase { Amplitude = 0.9, EasingMode = EasingMode.EaseOut });       // kurze Wege: ~30 % des Wegs drüber
    public static readonly IEasingFunction Expand = Ease(new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });    // lange Wege: ~5 % des Wegs drüber
    public static readonly IEasingFunction Glide = Ease(new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut });     // Verschieben
    public static readonly IEasingFunction Anticipate = Ease(new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseIn }); // holt erst Schwung
    public static readonly IEasingFunction Out = Ease(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction In = Ease(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction Swing = Ease(new SineEase { EasingMode = EasingMode.EaseInOut });

    // laufende Animation je Objekt und Eigenschaft: Eine ersetzte Animation darf beim Ende nichts mehr setzen
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, AnimationTimeline>> Running = new();
    private static readonly HashSet<UIElement> Pressed = new();

    // Eine Eigenschaft von → bis animieren. Schon während einer Wartezeit gilt der Startwert, am Ende bleibt der Zielwert
    // als normaler Wert stehen (die Animation wird entfernt), damit Code ihn später wieder setzen kann.
    public static void Run(IAnimatable target, DependencyProperty property, double from, double to, int ms,
                           IEasingFunction? ease = null, int delayMs = 0, Action? done = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? Out,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        Start(target, property, animation, from, to, done);
    }

    // Mehrere Stationen (Zeitpunkt in ms, Wert), weich verbunden. „settle“ formt den letzten Abschnitt, z. B. mit Nachfedern.
    public static void Keys(IAnimatable target, DependencyProperty property, int delayMs, (int Ms, double Value)[] keys,
                            IEasingFunction? settle = null, Action? done = null)
    {
        var animation = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        for (int i = 0; i < keys.Length; i++)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(keys[i].Value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(keys[i].Ms)),
                i == keys.Length - 1 && settle is not null ? settle : Swing));
        Start(target, property, animation, keys[0].Value, keys[^1].Value, done);
    }

    private static void Start(IAnimatable target, DependencyProperty property, AnimationTimeline animation, double from, double to, Action? done)
    {
        var owner = (DependencyObject)target;
        var running = Running.GetOrCreateValue(owner);
        if (!Enabled)
        {
            running.Remove(property);
            target.BeginAnimation(property, null);
            owner.SetValue(property, to);
            done?.Invoke();
            return;
        }
        running[property] = animation;
        animation.Completed += (_, _) =>
        {
            if (!running.TryGetValue(property, out var current) || current != animation) return;   // inzwischen ersetzt
            running.Remove(property);
            owner.SetValue(property, to);
            target.BeginAnimation(property, null);
            done?.Invoke();
        };
        owner.SetValue(property, from);
        target.BeginAnimation(property, animation);
    }

    // Skalierung und Verschiebung als RenderTransform: kostet kein Layout, nur Zeichnen
    public static ScaleTransform Scale(UIElement element) => Parts(element).Scale;

    public static TranslateTransform Shift(UIElement element) => Parts(element).Shift;

    private static (ScaleTransform Scale, TranslateTransform Shift) Parts(UIElement element)
    {
        if (element.RenderTransform is TransformGroup { IsFrozen: false } group && group.Children.Count == 2
            && group.Children[0] is ScaleTransform s && group.Children[1] is TranslateTransform t) return (s, t);
        var scale = new ScaleTransform();
        var shift = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { scale, shift } };
        return (scale, shift);
    }

    // Erscheinen: von kleiner über das Ziel hinaus, dann leicht zurück, dabei einblenden
    public static void PopIn(UIElement element, double from = 0.92, int ms = 420, double originX = 0.5, double originY = 0.5, int delayMs = 0)
    {
        element.RenderTransformOrigin = new Point(originX, originY);
        var scale = Scale(element);
        var ease = 1 - from > 0.2 ? Expand : Pop;                         // gleich viel Nachfedern, egal wie weit
        Run(scale, ScaleTransform.ScaleXProperty, from, 1, ms, ease, delayMs);
        Run(scale, ScaleTransform.ScaleYProperty, from, 1, ms, ease, delayMs);
        Run(element, UIElement.OpacityProperty, 0, 1, Math.Min(ms / 2, 200), Out, delayMs);
    }

    // Verschwinden: leicht schrumpfen und ausblenden, zum selben Ursprung wie beim Erscheinen
    public static void PopOut(UIElement element, double to = 0.94, int ms = 160, Action? done = null)
    {
        var scale = Scale(element);
        Run(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, to, ms, In);
        Run(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, to, ms, In);
        Run(element, UIElement.OpacityProperty, element.Opacity, 0, ms, In, done: done);
    }

    // Kurzes Nachfedern: breit und flach, dann zurück, z. B. beim Andocken
    public static void Bounce(UIElement element, double squash = 0.94, int ms = 420, double originX = 0.5, double originY = 0.5)
    {
        element.RenderTransformOrigin = new Point(originX, originY);
        var scale = Scale(element);
        Run(scale, ScaleTransform.ScaleXProperty, 2 - squash, 1, ms, Pop);
        Run(scale, ScaleTransform.ScaleYProperty, squash, 1, ms, Pop);
    }

    // Von etwas weiter unten einschweben, wie Zeilen, die nacheinander erscheinen (Sheet 3)
    public static void Rise(UIElement element, double dy = 10, int ms = 420, int delayMs = 0)
    {
        Run(Shift(element), TranslateTransform.YProperty, dy, 0, ms, Glide, delayMs);
        Run(element, UIElement.OpacityProperty, 0, 1, 220, Out, delayMs);
    }

    // Inhalte nacheinander statt alle auf einmal
    public static void Reveal(IEnumerable<UIElement> items, int delayMs = 0, int stepMs = 50, bool rise = false)
    {
        int i = 0;
        foreach (var item in items)
        {
            if (item.Visibility != Visibility.Visible) continue;
            if (rise) Rise(item, delayMs: delayMs + i * stepMs);
            else PopIn(item, 0.94, delayMs: delayMs + i * stepMs);
            i++;
        }
    }

    public static void After(int ms, Action action)
    {
        if (ms <= 0 || !Enabled) { action(); return; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    // ---------- Bedienung: gilt für alle Fenster ----------

    // Knöpfe und Listeneinträge geben beim Drücken nach und federn beim Loslassen zurück, Schalter gleiten mit Nachfedern
    public static void EnableInteractions()
    {
        foreach (var type in new[] { typeof(ButtonBase), typeof(ListBoxItem) })
        {
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((s, _) => Press((UIElement)s)), true);
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler((_, _) => { foreach (var e in Pressed.ToList()) Release(e); }), true);
            EventManager.RegisterClassHandler(type, UIElement.MouseLeaveEvent, new MouseEventHandler((s, _) => Release((UIElement)s)), true);
        }
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler(Slide));
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.UncheckedEvent, new RoutedEventHandler(Slide));
    }

    private static void Press(UIElement element)
    {
        if (element is RepeatButton || !Enabled || !Pressed.Add(element)) return;   // Regler und Scrollleisten nicht
        double depth = element is ListBoxItem ? 0.97 : 0.9;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = Scale(element);
        Run(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, depth, 90);
        Run(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, depth, 90);
    }

    private static void Release(UIElement element)
    {
        if (!Pressed.Remove(element)) return;
        var scale = Scale(element);
        Run(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, 1, 420, Pop);
        Run(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, 1, 420, Pop);
    }

    // Der Knopf springt per Stil sofort auf die neue Seite. Eine Verschiebung von der alten Seite aus lässt ihn sichtbar gleiten.
    private static void Slide(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { IsLoaded: true, IsVisible: true } box || box.Template?.FindName("Knob", box) is not FrameworkElement knob
            || knob.Parent is not FrameworkElement track) return;
        double travel = track.ActualWidth - knob.ActualWidth - knob.Margin.Left - knob.Margin.Right;
        Run(Shift(knob), TranslateTransform.XProperty, box.IsChecked == true ? -travel : travel, 0, 380, Glide);
        knob.RenderTransformOrigin = new Point(0.5, 0.5);
        Keys(Scale(knob), ScaleTransform.ScaleXProperty, 0, [(0, 1), (100, 1.3), (380, 1)], Pop);   // streckt sich unterwegs
    }

    private static IEasingFunction Ease(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}

// Zahl, die weich zum neuen Wert zählt, statt zu springen (wie der Betrag in Sheet 7). Höchstens 30 Bilder pro Sekunde.
internal sealed class Counter : Animatable
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Counter),
        new PropertyMetadata(0.0, (d, e) => ((Counter)d).Display?.Invoke((double)e.NewValue)));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Action<double>? Display { get; set; }          // zeigt den Wert an, auch während des Zählens

    public void CountTo(double target, int ms = 300)
    {
        if (!Motion.Enabled || Math.Abs(target - Value) < 0.5) { Stop(target); return; }
        var animation = new DoubleAnimation(Value, target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.Out };
        Timeline.SetDesiredFrameRate(animation, 30);
        BeginAnimation(ValueProperty, animation);
    }

    // sofort auf den Wert, ohne Zählen
    public void Stop(double value)
    {
        BeginAnimation(ValueProperty, null);
        Value = value;
        Display?.Invoke(value);
    }

    protected override Freezable CreateInstanceCore() => new Counter();
}
