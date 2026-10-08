using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Domestique.App;

public enum Mood { Idle, Search, Sleep, Happy, Dizzy, Focus, Strain }

// Der Avatar hat zwei Aufgaben: Er zeigt den Verbindungszustand (sucht, schläft ohne Daten, benommen bei Fehlern)
// und im Training die Anstrengung nach Leistungszone (locker, konzentriert, am Limit).
// Kosten: keine Dauer-Animation. Blinzeln sind zwei Neuzeichnungen alle paar Sekunden, nur solange er sichtbar ist.
public partial class Avatar : UserControl
{
    private static readonly Random Rng = new();
    private readonly DispatcherTimer _blink = new();
    private Mood _mood = (Mood)(-1);
    private bool _eyesShut;

    public Avatar()
    {
        InitializeComponent();
        _blink.Tick += (_, _) => Blink();
        IsVisibleChanged += (_, _) => { if (IsVisible) ScheduleBlink(); else _blink.Stop(); };
        Mood = Mood.Search;
        Recording = false;
    }

    public Mood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            bool open = value is Mood.Idle or Mood.Search or Mood.Focus;
            EyesOpen.Visibility = Show(open);
            Brows.Visibility = Show(value == Mood.Focus);
            EyesClosed.Visibility = Zzz.Visibility = Show(value == Mood.Sleep);
            EyesHappy.Visibility = Show(value == Mood.Happy);
            EyesDizzy.Visibility = Show(value == Mood.Dizzy);
            EyesStrain.Visibility = Sweat.Visibility = Show(value == Mood.Strain);
            Cheeks1.Visibility = Cheeks2.Visibility = Show(value is Mood.Happy or Mood.Dizzy or Mood.Strain);
            EyeShift.X = value == Mood.Search ? 3.5 : 0;               // schaut sich um
            EyeScale.ScaleY = OpenScale;
            _eyesShut = false;
        }
    }

    public bool Recording { set => RecDot.Visibility = Show(value); }

    private double OpenScale => _mood == Mood.Focus ? 0.75 : 1;

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

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
