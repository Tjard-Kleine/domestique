using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core;
using Domestique.Core.Control;
using Domestique.Core.Library;
using Domestique.Core.Physics;
using Domestique.Core.Protocol;
using Domestique.Core.Recording;
using Domestique.Core.Routes;

namespace Domestique.App;

public partial class MainWindow : Window
{
    private const int HotkeyClickThrough = 1;                 // Strg+Alt+D
    private const int HotkeyPowerUp = 2, HotkeyPowerDown = 3; // Strg+Alt+Bild↑ / Bild↓
    private const int HotkeyEndErg = 4;                       // Strg+Alt+Ende (nicht E: AltGr+E ist das €-Zeichen)
    private const int HotkeySettings = 6;                     // Strg+Alt+O
    private const double BaseWidth = 748;                     // 720 Panel + 2 × 14 Notch-Ohren
    private const double SnapDistance = 28, CenterSnap = 90;  // Andocken am oberen Rand, Einrasten in der Mitte
    private const double Overhang = 12;                       // durchsichtiger Rand unten (Root.Margin): Platz zum Nachfedern
    private const double TabStep = 36;                        // Breite eines Tabs in der Kopfzeile samt Abstand
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);
    private static readonly string RidesFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Domestique");
    private static readonly long AppStart = Stopwatch.GetTimestamp();     // monotone Zeitbasis für die Aufzeichnung

    // Zonenfarben wie bei Zwift
    private static readonly Brush[] ZoneBrushes = new[] { "#7F7F7F", "#7F7F7F", "#338CFF", "#59BF59", "#FFCC3F", "#FF6639", "#FF3B30" }
        .Select(c => (Brush)Frozen((Color)ColorConverter.ConvertFromString(c))).ToArray();

    private readonly SessionState _state = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LibraryStore _library = new(AppSettings.Folder);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };   // ein Takt für alles
    private readonly DispatcherTimer _toastTimer = new();
    private readonly Queue<(Mood Mood, string Title, string Text, double Seconds)> _toasts = new();
    private readonly ModeGuard _guard;
    private readonly RouteSim _sim = new(new RiderPhysics());
    private readonly GradeLimiter _limiter = new();
    private readonly RideRecorder _ride = new(RidesFolder);
    private readonly Counter _ergCounter = new();                         // ERG-Ziel zählt bei ± weich zum neuen Wert
    private readonly List<long> _pokes = new();                           // Zeitpunkte der letzten Stupser auf den Avatar
    private static readonly Random Rng = new();
    private BleTrainer? _trainer;
    private BleHeartRate? _strap;
    private SettingsWindow? _settingsWindow;
    private Func<RoutePoint, Point>? _mapProject, _profileProject;
    private bool _clickThrough, _closing, _trainerLoop, _strapLoop, _connectingTrainer, _connectingStrap;
    private bool _wasConnected, _everConnected, _hadControl, _controlLost, _strapWas, _wasFinished, _dockCentered;
    private string _message = "Suche Trainer …";
    private string? _lastTrainerError, _lastStrapError;
    private bool _bluetoothOff, _bluetoothCardDismissed, _bluetoothCardShown, _exiting;
    private Windows.Devices.Radios.Radio? _radio;                  // gehalten, sonst endet die Meldung beim Ein- und Ausschalten
    private TaskCompletionSource _wake = new();                    // weckt die Trainersuche, sobald Bluetooth an ist
    private string? _activeRoute;                                  // Datei, „app:Name“ oder null = keine Strecke
    private long _lastTick;
    private int _ticks;

    static MainWindow() => Motion.EnableInteractions();               // Drücken und Schalter in allen Fenstern

    public MainWindow()
    {
        InitializeComponent();
        _ergCounter.Display = v => ErgRun.Text = $"{v:0}";
        _guard = new ModeGuard(command => _trainer?.Control?.SetTarget(command));
        _timer.Tick += (_, _) => OnTick();
        _toastTimer.Tick += (_, _) => NextToast();
        SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged || e.PreviousSize.Height <= 0) return;
            if (_settings.Docked) Dock(center: _dockCentered);     // angedockt: oben bleiben
            else Top -= e.NewSize.Height - e.PreviousSize.Height;   // sonst bleibt der untere Rand stehen
        };
        // Streckenauswahl und ERG sind so hoch wie die Übersicht (die Liste scrollt), statt das Fenster zu vergrößern.
        // Die Übersicht ist oben ausgerichtet, ihre Höhe ist also ihre echte Höhe: Das Fenster schrumpft auch wieder.
        HomeView.SizeChanged += (_, _) => RouteView.Height = ErgView.Height = HomeView.ActualHeight;
    }

    public string TrainerStatus => _trainer is { } t && _state.Current.Connected ? $"{t.Name} · verbunden" : _message;
    public bool BluetoothOff => _bluetoothOff;                     // dieselbe Erkennung wie unter „Domestique“ im Overlay

    // Trainer und Pulsgurt zählen je einmal, die Steuerung läuft über den Trainer
    public int ConnectedDevices =>
        (_trainer is not null && _state.Current.Connected ? 1 : 0) + (_strap is not null && _state.Current.StrapConnected ? 1 : 0);

    public bool IsConnected(SavedDevice d) => d.Kind == "Trainer"
        ? _trainer is not null && _state.Current.Connected && _settings.TrainerAddress == d.Address
        : _strap is not null && _state.Current.StrapConnected && _settings.StrapAddress == d.Address;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = Native.Handle(this);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        RegisterHotkey(hwnd, HotkeyClickThrough, 0x44);       // D
        RegisterHotkey(hwnd, HotkeyPowerUp, 0x21);            // Bild↑ (nicht Pfeiltasten: drehen bei manchen Treibern den Bildschirm)
        RegisterHotkey(hwnd, HotkeyPowerDown, 0x22);          // Bild↓
        RegisterHotkey(hwnd, HotkeyEndErg, 0x23);             // Ende
        RegisterHotkey(hwnd, HotkeySettings, 0x4F);           // O
    }

    private static void RegisterHotkey(IntPtr hwnd, int id, uint key)
    {
        if (!Native.RegisterHotKey(hwnd, id, Native.MOD_CONTROL | Native.MOD_ALT, key))
            RawLog.Note($"Tastenkürzel Strg+Alt+0x{key:X2} ist von einem anderen Programm belegt");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RawLog.Note($"Domestique gestartet {DateTime.Now:yyyy-MM-dd}");
        ApplySettings();                                                   // Stil und Größe vor dem Positionieren
        if (_settings.Docked && _settings.NotchDock && _settings.Left is double dockLeft) { Left = dockLeft; Dock(center: IsCentered()); }
        else if (_settings.Left is double left && _settings.Top is double top && IsOnScreen(left, top)) { Left = left; Top = top; SetDocked(false); }
        else PlaceDefault();
        // Erst nach dem ersten gerenderten Bild aufklappen: Das erste Bild dauert beim Start spürbar (JIT, Grafik),
        // die Animation wäre sonst schon vorbei, bevor man etwas sieht. Bis dahin bleibt das Fenster unsichtbar.
        Root.Opacity = 0;
        ContentRendered += (_, _) => PlayOpen();
        _radio = await BluetoothRadio.WatchAsync(on => Dispatcher.BeginInvoke(() => OnBluetoothChanged(on)));
        if (_radio is { State: not Windows.Devices.Radios.RadioState.On }) OnBluetoothChanged(false);   // schon beim Start aus
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);   // Review #9
        Native.KeepTopmost(this);                                           // Review #7
        RideRecorder.RecoverUnexported(RidesFolder);
        Render(_state.Current);
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
        _ = KeepTrainerConnectedAsync();
    }

    // ---------- Verbindungen ----------

    // Sucht und verbindet, bis es klappt: Der Trainer darf auch erst nach der App eingeschaltet werden.
    private async Task KeepTrainerConnectedAsync()
    {
        if (_trainerLoop) return;
        _trainerLoop = true;
        try
        {
            while (_trainer is null && !_closing)
            {
                if (await TryConnectTrainerAsync()) break;
                if (!_strapLoop) _ = KeepStrapConnectedAsync();             // Gurt erst nach dem ersten Trainer-Versuch
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10)), _wake.Task);   // Bluetooth an: sofort weiter
                _wake = new();
            }
        }
        finally { _trainerLoop = false; }
        if (!_strapLoop) _ = KeepStrapConnectedAsync();
    }

    private async Task<bool> TryConnectTrainerAsync()
    {
        if (_connectingTrainer || _trainer is not null) return _trainer is not null;
        _connectingTrainer = true;
        var trainer = new BleTrainer(_state);
        try
        {
            _message = "Suche Trainer …";
            ulong? address = _settings.TrainerAddress
                ?? await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            _bluetoothOff = false;                                          // die Suche lief, also ist Bluetooth an
            if (address is null) { _message = "Kein Trainer gefunden"; return false; }
            _message = "Verbinde …";
            await trainer.ConnectAsync(address.Value);
            _trainer = trainer;
            _lastTrainerError = null;
            _settings.TrainerAddress = address;
            _settings.Remember(address.Value, trainer.Name, "Trainer");   // erscheint unter „Meine Geräte“
            _settings.Save();
            if (trainer.PowerRange is { } range) _guard.PowerLimits = range;
            _guard.Reapply();                                               // Startzustand flache Straße oder das schon gewählte Ziel
            _message = "Warte auf Trainer …";                               // erscheint nur, falls die Verbindung später abreißt
            return true;
        }
        catch (Exception ex)
        {
            await trainer.DisposeAsync();                                   // halbe Verbindung freigeben
            if (ex.Message != _lastTrainerError) RawLog.Note("Trainer: " + ex.Message);   // bei Dauerfehlern nicht alle 10 s
            _lastTrainerError = ex.Message;
            _bluetoothOff = ex.Message.Contains("Bluetooth");
            _message = _bluetoothOff ? "Bluetooth ist aus" : "Verbindung fehlgeschlagen";
            _settings.TrainerAddress = null;                                // beim nächsten Versuch neu suchen
            _settings.Save();
            return false;
        }
        finally { _connectingTrainer = false; }
    }

    // Puls vom Gurt hat Vorrang vor dem Puls des Trainers. Ohne Gurt läuft alles wie bisher.
    // Ein bekannter Gurt wird alle 30 s erneut versucht (z. B. erst nach dem Start angelegt). Ohne bekannten Gurt
    // wird nur einmal gesucht, weil dauerndes Scannen die laufende Trainer-Verbindung stören kann.
    private async Task KeepStrapConnectedAsync()
    {
        if (_strapLoop) return;
        _strapLoop = true;
        try
        {
            while (_strap is null && !_closing)
            {
                if (await TryConnectStrapAsync() || _settings.StrapAddress is null) break;
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        finally { _strapLoop = false; }
    }

    private async Task<bool> TryConnectStrapAsync()
    {
        if (_connectingStrap || _strap is not null) return _strap is not null;
        _connectingStrap = true;
        var strap = new BleHeartRate(_state);
        try
        {
            ulong? address = _settings.StrapAddress
                ?? await BleScan.FindFirstAsync(BleUuids.HeartRateService, TimeSpan.FromSeconds(10));
            if (address is null) return false;                              // kein Gurt in der Nähe
            await strap.ConnectAsync(address.Value);
            _strap = strap;
            _settings.StrapAddress = address;
            _settings.Remember(address.Value, strap.Name, "Pulsgurt");
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            await strap.DisposeAsync();                                     // Adresse bleibt: Gurt evtl. nur noch nicht angelegt
            if (ex.Message != _lastStrapError) RawLog.Note("Pulsgurt: " + ex.Message);
            _lastStrapError = ex.Message;
            return false;
        }
        finally { _connectingStrap = false; }
    }

    public async Task<bool> ReconnectTrainerAsync()
    {
        if (_trainer is not null) { await _trainer.DisposeAsync(); _trainer = null; }
        _message = "Verbinde neu …";
        bool ok = await TryConnectTrainerAsync();                          // der Modus-Wächter schickt dem neuen Trainer das aktuelle Ziel
        if (!ok) _ = KeepTrainerConnectedAsync();
        return ok;
    }

    public async Task<bool> ReconnectStrapAsync()
    {
        if (_strap is not null) { await _strap.DisposeAsync(); _strap = null; }
        bool ok = await TryConnectStrapAsync();
        if (!ok) _ = KeepStrapConnectedAsync();
        return ok;
    }

    // ---------- Takt: Physik 4×/s, Anzeige 2×/s, Steigung, Karte und Aufzeichnung 1×/s ----------

    private void OnTick()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds, 1);
        _lastTick = now;

        var s = _state.Current;
        bool stale = IsStale(s);
        _sim.Step(stale ? 0 : s.PowerW, dt);                                // Review #5: ohne Daten keine alten Watt
        _state.Update(x => x with
        {
            VirtualSpeedKmh = _sim.SpeedMps * 3.6,
            DistanceM = _sim.DistanceM,
            GradePercent = _sim.GradePercent,
        });
        Record(_state.Current, stale);

        _ticks++;
        if (_ticks % 2 == 0) Render(_state.Current);
        if (_ticks % 4 != 0) return;
        // ohne Strecke ist die Steigung 0: nach „Keine Strecke“ geht der Trainer sanft auf flach zurück
        _guard.SetGrade(_limiter.Next(_sim.GradePercent * _settings.Difficulty, 1));       // im ERG ignoriert (Review #1)
        MoveDots();
    }

    private static bool IsStale(SessionSnapshot s) => Stopwatch.GetElapsedTime(s.LastPacketTimestamp) > StaleAfter;

    // ---------- Anzeige: nur die sichtbare Ansicht wird aktualisiert ----------

    private void Render(SessionSnapshot s)
    {
        bool stale = IsStale(s);
        bool controlLost = s.Connected && _trainer?.Control is { HasControl: false };
        int zone = stale ? 0 : PowerZones.Of(s.Power3sW, _settings.FtpW);
        DetectEvents(s, controlLost);

        // Kopfzeile: Verbindungspunkt und Anzahl verbundener Geräte, immer sichtbar. Ändert sich etwas, springt der Punkt kurz.
        if (SetFill(LinkDot, !s.Connected ? "MutedBrush" : stale || controlLost ? "WarningBrush" : "PositiveBrush")) Motion.PopIn(LinkDot, 0.3, 380);
        string count = ConnectedDevices.ToString();
        if (LinkCount.Text != count) { LinkCount.Text = count; Motion.Bounce(LinkButton, 0.85); }
        LinkButton.ToolTip = $"{TrainerStatus}\n{(s.StrapConnected ? "Pulsgurt verbunden" : "kein Pulsgurt")}\nKlick: Bluetooth-Einstellungen";

        UpdateBluetoothCard();
        // nach dem gewählten Tab, nicht nach der Sichtbarkeit: Beim Wechsel blendet die alte Ansicht noch kurz aus
        if (HomeTab.IsChecked == true) RenderHome(s, stale, controlLost, zone);
        else if (ErgTab.IsChecked == true) RenderErg();
    }

    private void RenderHome(SessionSnapshot s, bool stale, bool controlLost, int zone)
    {
        Mascot.Mood = !s.Connected ? (_message == "Bluetooth ist aus" ? Mood.Dizzy : Mood.Search)
            : stale ? Mood.Sleep
            : controlLost ? Mood.Dizzy
            : zone >= 5 ? Mood.Strain
            : zone >= 3 ? Mood.Focus
            : Mood.Idle;
        Mascot.Recording = _ride.Current is not null;
        Mascot.Busy = !s.Connected && !_bluetoothOff;                      // „…“-Abzeichen, solange er sucht
        // Bluetooth aus zeigt die große Karte, die Statuszeile bleibt dann leer
        StatusLine.Visibility = _bluetoothOff && !s.Connected ? Visibility.Hidden : Visibility.Visible;
        SubtitleText.Text = !s.Connected ? _message
            : stale ? "keine Daten"
            : controlLost ? "Steuerung verloren"
            : _sim.Finished ? "Ziel erreicht"
            : _trainer?.Name ?? "Trainer";
        if (SetFill(StatusDot, !s.Connected ? "MutedBrush" : stale || controlLost ? "WarningBrush" : "PositiveBrush")) Motion.PopIn(StatusDot, 0.3, 380);

        // die drei wichtigsten Werte, immer sichtbar; „–“, wenn der Trainer sie nicht liefert (D100: keine Trittfrequenz).
        // Live-Werte springen wie bei Zwift: Ein Hochzählen bei jedem Wert würde das Overlay ständig neu zeichnen (CPU).
        PowerRun.Text = stale ? "–" : s.Power3sW.ToString();
        CadenceRun.Text = stale || s.CadenceRpm is null ? "–" : $"{s.CadenceRpm:0}";
        HeartRun.Text = s.HeartRateBpm?.ToString() ?? "–";
        var chip = _guard.TargetPowerW is null ? Visibility.Collapsed : Visibility.Visible;
        if (chip != TargetChip.Visibility && chip == Visibility.Visible) Motion.PopIn(TargetChip, 0.6, 420, 0, 0.5);
        TargetChip.Visibility = chip;
        TargetChipText.Text = $"ZIEL {_guard.TargetPowerW} W";
        ShowZone(zone);

        SpeedRun.Text = $"{s.VirtualSpeedKmh:0.0}";
        DistanceRun.Text = $"{s.DistanceM / 1000:0.00}";
        ClimbRun.Text = $"{_sim.ClimbedM:0}";
        var t = _sim.MovingTime;                                            // zählt nur, solange Watt anliegen
        TimeValue.Text = $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        RenderRoute(s);
    }

    // Zonenleiste unter dem Avatar: aktuelle Zone voll in Zwift-Farbe, die übrigen blass. Beim Wechsel springt das neue
    // Segment kurz auf und der Avatar nimmt ab Zone 2 weich die Zonenfarbe an (wie die Umfärbung in Sheet 7).
    private void ShowZone(int zone)
    {
        if ((ZoneBar.Tag as int?) == zone) return;
        ZoneBar.Tag = zone;
        for (int i = 0; i < 6; i++)
        {
            var segment = (Border)ZoneBar.Children[i];
            segment.Background = ZoneBrushes[i + 1];
            segment.Opacity = i + 1 == zone ? 1 : 0.22;
            if (i + 1 != zone) continue;
            segment.RenderTransformOrigin = new Point(0.5, 0.5);
            Motion.Run(Motion.Scale(segment), ScaleTransform.ScaleYProperty, 1.9, 1, 420, Motion.Pop);
        }
        ZoneText.Text = zone > 0 ? $"ZONE {zone}" : "–";
        Mascot.Tint(zone >= 2 ? ((SolidColorBrush)ZoneBrushes[zone]).Color : null);
    }

    // Strecke auf der Übersicht, rechts neben den drei Hauptwerten
    private void RenderRoute(SessionSnapshot s)
    {
        RouteLoaded.Visibility = _sim.Route is null ? Visibility.Collapsed : Visibility.Visible;
        StatsGrid.Columns = RouteLoaded.Visibility == Visibility.Visible && RouteGraphics.Visibility == Visibility.Visible ? 4 : 2;
        if (_sim.Route is not { } r) return;
        double done = Math.Min(_sim.RouteDistanceM, r.LengthM);
        RouteNameText.Text = $"{r.Name.ToUpperInvariant()} · {done / 1000:0.0} / {r.LengthM / 1000:0.0} KM";
        GradeValue.Text = $"{s.GradePercent:+0.0;-0.0;0.0} %";
    }

    private void RenderErg()
    {
        bool erg = _guard.Mode == ControlMode.Erg;
        if (_guard.TargetPowerW is not int target) { if (ErgRun.Text != "–") { _ergCounter.Stop(0); ErgRun.Text = "–"; } }
        else if (ErgRun.Text == "–") _ergCounter.Stop(target);
        else _ergCounter.CountTo(target);                                  // ± zählt sichtbar hoch und runter
        ModeText.Text = erg ? "ERG AKTIV" : _guard.Mode == ControlMode.Sim ? "SIMULATION" : "FREI";
        if ((ModeChip.Tag as bool?) != erg)
        {
            ModeChip.Tag = erg;
            ModeChip.SetResourceReference(Border.BackgroundProperty, erg ? "AccentBrush" : "ChipBrush");
            ModeText.SetResourceReference(TextBlock.ForegroundProperty, erg ? "OnAccentBrush" : "TextBrush");
        }
        ErgToggleButton.Content = erg ? "ERG beenden" : "ERG starten";
        var (min, max) = _guard.PowerLimits;
        ErgHint.Text = erg ? $"Bereich {min}–{max} W"                       // Tastenkürzel stehen im Tooltip von − und +
            : $"Simulation: {_limiter.Current:+0.0;-0.0;0.0} % am Trainer";
    }

    // Brush aus dem Stil setzen, der beim Stilwechsel mitwechselt. Nur bei Änderung, damit nichts neu gezeichnet wird.
    // true, wenn sich die Farbe geändert hat (nicht beim ersten Setzen).
    private static bool SetFill(System.Windows.Shapes.Shape shape, string key)
    {
        if ((string?)shape.Tag == key) return false;
        bool first = shape.Tag is null;
        shape.Tag = key;
        shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        return !first;
    }

    // ---------- Bluetooth aus ----------

    // Windows meldet das Ein- und Ausschalten sofort. Ein: Karte weg und gleich weitersuchen.
    private void OnBluetoothChanged(bool on)
    {
        _bluetoothOff = !on;
        _message = on ? "Suche Trainer …" : "Bluetooth ist aus";
        if (!on) { _bluetoothCardDismissed = false; Render(_state.Current); return; }
        _wake.TrySetResult();                                              // Trainersuche wartet nicht die 10 s ab
        if (!_strapLoop) _ = KeepStrapConnectedAsync();
        Render(_state.Current);
    }

    // Die Karte bleibt stehen, bis man tippt oder Bluetooth einschaltet. Sie springt auf wie eine Meldung.
    private void UpdateBluetoothCard()
    {
        bool show = _bluetoothOff && !_bluetoothCardDismissed;
        if (show == _bluetoothCardShown) return;
        _bluetoothCardShown = show;
        if (show)
        {
            BluetoothAvatar.Mood = Mood.Dizzy;
            BluetoothCard.Visibility = Visibility.Visible;
            Motion.PopIn(BluetoothCard, 0.94);
            Motion.PopIn(BluetoothAvatar, 0.5, 480, 0.5, 1, delayMs: 60);
            BluetoothAvatar.Shake(320);
            Glow(BluetoothGlow, Mood.Dizzy);
        }
        else Motion.PopOut(BluetoothCard, 0.96, 160, () => { if (!_bluetoothCardShown) BluetoothCard.Visibility = Visibility.Collapsed; });
    }

    // Tippen öffnet die Bluetooth-Seite der Windows-Einstellungen
    private void BluetoothCard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        _bluetoothCardDismissed = true;
        UpdateBluetoothCard();
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception ex) { RawLog.Note("Bluetooth-Einstellungen nicht geöffnet: " + ex.Message); }
    }

    // ---------- Meldungen ----------

    private void DetectEvents(SessionSnapshot s, bool controlLost)
    {
        if (s.Connected != _wasConnected)
        {
            if (s.Connected) Mascot.Done();                                 // Abzeichen wird grün (Sheet 6)
            if (s.Connected) ShowToast(Mood.Happy, $"{_trainer?.Name ?? "Trainer"} verbunden",
                _everConnected ? "Wieder da. Ziel und Steuerung sind zurück." : "Los geht's, ich halte die Verbindung.");
            else if (_everConnected && !_closing) ShowToast(Mood.Dizzy, "Verbindung weg", "Ich warte, bis der Trainer wieder da ist.", 6);
            _everConnected |= s.Connected;
            _wasConnected = s.Connected;
        }
        bool hasControl = s.Connected && _trainer?.Control is { HasControl: true };
        if (controlLost && _hadControl) { _controlLost = true; ShowToast(Mood.Dizzy, "Steuerung verloren", "Eine andere App greift auf den Trainer zu. Ich hole sie zurück.", 6); }
        if (hasControl && _controlLost) { _controlLost = false; ShowToast(Mood.Happy, "Steuerung zurück", "Der Trainer hört wieder auf mich."); }
        _hadControl = hasControl;
        if (s.StrapConnected && !_strapWas) ShowToast(Mood.Happy, "Pulsgurt verbunden", "Der Puls kommt jetzt vom Gurt.");
        _strapWas = s.StrapConnected;
        if (_sim.Finished && !_wasFinished && _sim.Route is { } r)
            ShowToast(Mood.Happy, "Ziel erreicht!", $"{r.Name} · {r.LengthM / 1000:0.0} km geschafft.", 8);
        _wasFinished = _sim.Finished;
    }

    private void ShowToast(Mood mood, string title, string text, double seconds = 4)
    {
        _toasts.Enqueue((mood, title, text, seconds));
        if (Toast.Visibility != Visibility.Visible) NextToast();
    }

    // Meldung springt auf, der Avatar zuerst, dann die Zeilen nacheinander (Sheet 9). Gute Nachrichten: Er winkt.
    // Probleme: Er schüttelt sich, darunter pulsiert ein roter Schein (Sheet 10). Am Ende schrumpft sie weg.
    private void NextToast()
    {
        _toastTimer.Stop();
        if (_toasts.TryDequeue(out var t))
        {
            ToastAvatar.Mood = t.Mood;
            ToastTitle.Text = t.Title;
            ToastText.Text = t.Text;
            Toast.Visibility = Visibility.Visible;
            Motion.PopIn(Toast, 0.94);
            Motion.PopIn(ToastAvatar, 0.5, 480, 0.5, 1, delayMs: 40);
            Motion.Reveal([ToastTitle, ToastText], 120, 70, rise: true);
            Glow(ToastGlow, t.Mood);
            if (t.Mood == Mood.Happy) ToastAvatar.Wave(380, 2);
            else if (t.Mood == Mood.Dizzy) ToastAvatar.Shake(320);
            _toastTimer.Interval = TimeSpan.FromSeconds(t.Seconds);
            _toastTimer.Start();
            return;
        }
        if (Toast.Visibility != Visibility.Visible) return;
        Motion.PopOut(Toast, 0.96, 160, () =>
        {
            Toast.Visibility = Visibility.Collapsed;
            if (_toasts.Count > 0) NextToast();                            // kam während des Ausblendens dazu
        });
    }

    // Schein am unteren Rand: rot pulsierend bei Problemen, sonst ruhig in Grün oder Akzentfarbe
    private static void Glow(System.Windows.Shapes.Shape glow, Mood mood)
    {
        glow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            mood == Mood.Dizzy ? "NegativeBrush" : mood == Mood.Happy ? "PositiveBrush" : "AccentBrush");
        if (mood == Mood.Dizzy)
            Motion.Keys(glow, OpacityProperty, 0, [(0, 0), (350, 0.9), (800, 0.45), (1250, 0.9), (1700, 0.45), (2150, 0.9), (2700, 0.6)]);
        else Motion.Run(glow, OpacityProperty, 0, 0.5, 500);
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        NextToast();
    }

    // ---------- Ansichten ----------

    // Hidden statt Collapsed: Die verborgenen Ansichten behalten ihren Platz, so springt die Fenstergröße nicht.
    // Wechsel wie in Sheet 3 und 7: Der alte Inhalt dimmt kurz ab, der neue erscheint Teil für Teil, wächst etwas
    // über seine Größe hinaus und schnappt zurück. Die Auswahl gleitet zum neuen Tab.
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomeView is null || RouteView is null || ErgView is null) return;   // während InitializeComponent
        FrameworkElement next = HomeTab.IsChecked == true ? HomeView : RouteTab.IsChecked == true ? RouteView : ErgView;
        foreach (var view in new FrameworkElement[] { HomeView, RouteView, ErgView })
        {
            if (view == next || view.Visibility != Visibility.Visible) continue;
            Motion.Run(view, OpacityProperty, view.Opacity, 0, 120, done: () =>
            {
                if (view == CurrentView) return;                           // inzwischen wieder gewählt
                view.Visibility = Visibility.Hidden;
                view.Opacity = 1;
            });
        }
        next.Visibility = Visibility.Visible;
        Motion.Run(next, OpacityProperty, next.Opacity, 1, 120);
        if (RouteTab.IsChecked == true) RefreshRouteList();
        Render(_state.Current);
        MoveDots();
        Motion.Reveal(RevealParts(next), 60, 55);
        if (next == RouteView) RevealRouteItems(160);

        var pill = Motion.Shift(TabPill);
        double x = (HomeTab.IsChecked == true ? 0 : RouteTab.IsChecked == true ? 1 : 2) * TabStep;
        Motion.Run(pill, TranslateTransform.XProperty, pill.X, x, 380, Motion.Glide);
        TabPill.RenderTransformOrigin = new Point(0.5, 0.5);
        Motion.Keys(Motion.Scale(TabPill), ScaleTransform.ScaleXProperty, 0, [(0, 1), (110, 1.3), (400, 1)], Motion.Pop);   // streckt sich unterwegs
    }

    private FrameworkElement CurrentView => HomeTab.IsChecked == true ? HomeView : RouteTab.IsChecked == true ? RouteView : ErgView;

    // Teile einer Ansicht in der Reihenfolge, in der sie erscheinen
    private IEnumerable<UIElement> RevealParts(FrameworkElement view)
    {
        if (view == HomeView)
        {
            yield return RideCard;
            yield return RouteLoaded;
            foreach (UIElement tile in StatsGrid.Children) yield return tile;
        }
        else if (view == RouteView)
        {
            yield return RouteBar;
            yield return AddPanel;
        }
        else yield return ErgView;
    }

    // Strecken der Liste nacheinander, wie Zeilen, die getippt werden (Sheet 3)
    private void RevealRouteItems(int delayMs)
    {
        if (RouteList.Visibility != Visibility.Visible) return;
        RouteList.UpdateLayout();                                           // Einträge erzeugen
        var items = Enumerable.Range(0, Math.Min(RouteList.Items.Count, 12))
            .Select(i => RouteList.ItemContainerGenerator.ContainerFromIndex(i)).OfType<UIElement>();
        Motion.Reveal(items, delayMs, 35, rise: true);
    }

    // ---------- Aufzeichnung (Phase 7) ----------

    // Speed und Distanz aus der Physik, nicht vom Trainer (Review #12).
    private void Record(SessionSnapshot s, bool stale) =>
        _ride.Tick(Stopwatch.GetElapsedTime(AppStart), DateTime.UtcNow, stale ? 0 : s.PowerW, stale ? 0 : s.CadenceRpm ?? 0,
                   s.HeartRateBpm, _sim.SpeedMps, s.DistanceM);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_ride.Export() is { } file) ShowToast(Mood.Happy, "Fahrt gespeichert", Path.GetFileName(file) + " liegt in Dokumente\\Domestique.", 6);
            else ShowToast(Mood.Sleep, "Noch keine Fahrt", "Die Aufzeichnung startet mit dem ersten Tritt.");
        }
        catch (Exception ex) { ShowToast(Mood.Dizzy, "Export fehlgeschlagen", ex.Message, 8); }
    }

    // ---------- Strecken und Karte (Phase 5, 6, 9) ----------

    // Ein Eintrag der Streckenauswahl. Key: Datei, „app:Name“ oder null für „Keine Strecke“.
    public sealed record RouteItem(string? Key, string Label, string Icon, string Detail);

    private const string NoRouteIcon = "", RouteIcon = "";

    // Kontextmenü „Strecke laden …“: öffnet die Streckenauswahl zum Hinzufügen
    private void LoadRoute_Click(object sender, RoutedEventArgs e)
    {
        RouteTab.IsChecked = true;
        AddSegment.IsChecked = true;
    }

    private void RouteMode_Checked(object sender, RoutedEventArgs e)
    {
        if (AddPanel is null || RouteList is null) return;                 // während InitializeComponent
        bool add = AddSegment.IsChecked == true;
        AddPanel.Visibility = add ? Visibility.Visible : Visibility.Collapsed;
        RouteList.Visibility = ConfirmRouteButton.Visibility = add ? Visibility.Collapsed : Visibility.Visible;
        SourceBar.Visibility = add ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RouteSource_Checked(object sender, RoutedEventArgs e) => RefreshRouteList();

    // „Keine Strecke“ steht immer oben, darunter eigene oder mitgelieferte Strecken. Die aktive ist vorausgewählt.
    private void RefreshRouteList(string? select = null)
    {
        if (RouteList is null) return;
        var items = new List<RouteItem> { new(null, "Keine Strecke", NoRouteIcon, "flach") };
        if (AppRoutesSegment.IsChecked == true)
            items.AddRange(BuiltInRoutes.Names.Select(n => new RouteItem("app:" + n, n, RouteIcon, $"{BuiltInRoutes.LengthKm(n):0} km")));
        else
            items.AddRange(_library.Routes.Select(f => new RouteItem(f, Path.GetFileNameWithoutExtension(f), RouteIcon, "")));
        RouteList.ItemsSource = items;
        string? wanted = select ?? _activeRoute;
        RouteList.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Key, wanted, StringComparison.OrdinalIgnoreCase)) ?? items[0];
    }

    private void PickRouteFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX-Strecke (*.gpx)|*.gpx" };
        if (dialog.ShowDialog(this) == true) ImportRoute(dialog.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            foreach (var file in files) ImportRoute(file);
    }

    // Neue Datei landet in der Bibliothek und wird dort ausgewählt. Festgelegt wird sie erst mit „Strecke festlegen“.
    private void ImportRoute(string path)
    {
        try
        {
            string imported = _library.Import(path);
            RouteTab.IsChecked = true;
            OwnRoutesSegment.IsChecked = true;
            LibrarySegment.IsChecked = true;
            RefreshRouteList(select: imported);
            RouteList.ScrollIntoView(RouteList.SelectedItem);
        }
        catch (Exception ex) { ShowToast(Mood.Dizzy, "Datei nicht lesbar", ex.Message, 8); }
    }

    private void ConfirmRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteList.SelectedItem is not RouteItem item) return;
        if (item.Key is null) UnloadRoute();
        else if (item.Key.StartsWith("app:")) ShowRoute(BuiltInRoutes.Load(item.Key[4..]), item.Key);
        else LoadFile(item.Key);
    }

    public void LoadFile(string path)
    {
        try { ShowRoute(GpxLoader.Load(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path)), path); }
        catch (Exception ex) { ShowToast(Mood.Dizzy, "Strecke nicht lesbar", ex.Message, 8); }
    }

    // Die Strecke erscheint auf der Übersicht
    private void ShowRoute(Route route, string key)
    {
        _sim.Load(route);
        _activeRoute = key;
        MapLine.Points = RouteGeometry.Map(route, 72, 72, out _mapProject);
        ProfileLine.Points = RouteGeometry.Profile(route, 248, 72, out _profileProject);
        ProfileArea.Points = RouteGeometry.Area(ProfileLine.Points, 72);
        HomeTab.IsChecked = true;
        Render(_state.Current);
        MoveDots();
        ShowToast(Mood.Happy, "Strecke geladen", $"{route.Name} · {route.LengthM / 1000:0.0} km");
        RawLog.Note($"Strecke geladen: {route.Name}, {route.LengthM / 1000:0.0} km");
    }

    private void UnloadRoute()
    {
        bool had = _sim.Route is not null;
        _sim.Unload();
        _activeRoute = null;
        _mapProject = _profileProject = null;
        HomeTab.IsChecked = true;
        Render(_state.Current);
        if (had) ShowToast(Mood.Idle, "Keine Strecke", "Freie Fahrt: Es geht flach weiter, der Trainer wird wieder leicht.");
    }

    private void MoveDots()
    {
        if (_sim.Route is not { } route || _mapProject is null || _profileProject is null
            || HomeView.Visibility != Visibility.Visible || RouteGraphics.Visibility != Visibility.Visible) return;
        double distance = Math.Min(_sim.RouteDistanceM, route.LengthM);
        var p = route.At(distance);
        var m = _mapProject(p);
        Canvas.SetLeft(MapDot, m.X - 4);
        Canvas.SetTop(MapDot, m.Y - 4);
        var q = _profileProject(p);
        Canvas.SetLeft(ProfileDot, q.X - 4);
        Canvas.SetTop(ProfileDot, q.Y - 4);
        // gefahrener Teil in beiden Karten markiert
        MapDone.Points = RouteGeometry.Done(MapLine.Points, route, distance, _mapProject);
        ProfileDone.Points = RouteGeometry.Done(ProfileLine.Points, route, distance, _profileProject);
        ProfileDoneArea.Points = RouteGeometry.Area(ProfileDone.Points, 72);
    }

    // ---------- Einstellungen (Phase 9) ----------

    public void ApplySettings()
    {
        _settings.Theme = App.ApplyTheme(_settings.Theme);
        var surface = (Color)FindResource("SurfaceColor");
        double minOpacity = TryFindResource("MinOpacity") as double? ?? 0.2;                  // ganz durchsichtig wäre unlesbar
        surface.A = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, minOpacity, 1) * 255);
        var background = Frozen(surface);
        Shell.Background = LeftEar.Fill = RightEar.Fill = background;

        double scale = Math.Clamp(_settings.Scale, 0.5, 2);
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
        RouteGraphics.Visibility = _settings.ShowMap ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.NotchDock && _settings.Docked) { SetDocked(false); Top += 12; }
        else SetDocked(_settings.Docked);                                  // Ecken passend zum Stil

        _sim.Physics.MassKg = _settings.RiderKg + _settings.BikeKg;
        _sim.Physics.CdA = Math.Clamp(_settings.CdA, 0.1, 1);
        Render(_state.Current);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OpenSettings(bool bluetooth = false)
    {
        if (_clickThrough) SetClickThrough(false);
        if (_settingsWindow is { IsLoaded: true })
        {
            if (bluetooth) _settingsWindow.ShowBluetooth();
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, _settings, _library);
        if (bluetooth) _settingsWindow.ShowBluetooth();
        // neben dem Overlay aufklappen wie ein Popover: darunter, wenn oben Platz ist, sonst darüber.
        // Beide Fenster haben einen durchsichtigen Rand zum Nachfedern, gerechnet wird mit den sichtbaren Flächen.
        var screen = Native.MonitorBounds(this);
        double padX = SettingsWindow.PadX, padY = SettingsWindow.PadY;
        double width = _settingsWindow.Width - 2 * padX, height = _settingsWindow.Height - 2 * padY;
        double left = Math.Clamp(Left + ActualWidth - width - 14, screen.Left + 8, screen.Right - width - 8);
        double below = Top + ActualHeight - Overhang + 8, above = Top - height - 8;
        _settingsWindow.Left = left - padX;
        _settingsWindow.Top = (below + height <= screen.Bottom ? below : Math.Max(screen.Top + 8, above)) - padY;
        _settingsWindow.Show();
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void LinkButton_Click(object sender, RoutedEventArgs e) => OpenSettings(bluetooth: true);

    // ---------- Position und Notch ----------

    // Ziehen und loslassen: nah am oberen Rand dockt das Overlay an, nah der Mitte rastet es zentriert ein.
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var before = new Point(Left, Top);
        bool wasDocked = _settings.Docked;
        DragMove();
        if (!_settings.NotchDock || before == new Point(Left, Top)) return;    // nur Klick, nicht verschoben
        var screen = Native.MonitorBounds(this);
        if (Top - screen.Top < SnapDistance) { DockAnimated(center: Math.Abs(Left + ActualWidth / 2 - screen.Left - screen.Width / 2) < CenterSnap); return; }
        SetDocked(false);
        if (wasDocked) Motion.Bounce(Shell, 0.97, 380);                    // löst sich und federt kurz
        _settings.Left = Left;
        _settings.Top = Top;
    }

    // Andocken wie ein Magnet: gleitet an den Rand (und in die Mitte), dann wachsen die Notch-Ohren heraus
    // und das Panel federt kurz nach, als würde es einrasten
    private void DockAnimated(bool center)
    {
        var screen = Native.MonitorBounds(this);
        double left = center ? screen.Left + (screen.Width - ActualWidth) / 2 : Left;
        _settings.Left = left;
        _settings.Top = screen.Top;
        if (center) Motion.Run(this, LeftProperty, Left, left, 200);
        Motion.Run(this, TopProperty, Top, screen.Top, 200, done: () =>
        {
            Dock(center);
            Motion.Bounce(Shell, 0.97, 420, 0.5, 0);
            Motion.PopIn(LeftEar, 0, 340, 1, 0);
            Motion.PopIn(RightEar, 0, 340, 0, 0);
        });
    }

    private void Dock(bool center)
    {
        var screen = Native.MonitorBounds(this);
        Top = screen.Top;
        if (center) Left = screen.Left + (screen.Width - ActualWidth) / 2;
        _dockCentered = center;
        SetDocked(true);
    }

    private bool IsCentered()
    {
        var screen = Native.MonitorBounds(this);
        return Math.Abs(Left + ActualWidth / 2 - screen.Left - screen.Width / 2) < 2;
    }

    // Angedockt: oben eckig und ohne Rahmen, mit den geschwungenen Notch-Ohren. Frei: rundum abgerundet.
    private void SetDocked(bool docked)
    {
        _settings.Docked = docked;
        var r = (CornerRadius)FindResource("ShellRadius");
        Shell.CornerRadius = docked ? new CornerRadius(0, 0, r.BottomRight, r.BottomLeft) : r;
        Shell.BorderThickness = docked ? new Thickness(0) : new Thickness(1);
        LeftEar.Visibility = RightEar.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ResetPosition()
    {
        _settings.Left = _settings.Top = null;
        PlaceDefault();
    }

    // Mit Notch oben in der Mitte, sonst unten rechts
    private void PlaceDefault()
    {
        UpdateLayout();
        if (_settings.NotchDock) { Dock(center: true); return; }
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight + Overhang - 16;
        SetDocked(false);
    }

    // Gespeicherte Position auf einem inzwischen abgesteckten Monitor: lieber neu platzieren.
    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    // ---------- Tastenkürzel, Buttons und Menü ----------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY) { OnHotkey(wParam.ToInt32()); handled = true; }
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyClickThrough: SetClickThrough(!_clickThrough); break;
            case HotkeyPowerUp:   PowerStep(+10); break;
            case HotkeyPowerDown: PowerStep(-10); break;
            case HotkeyEndErg:    EndErg(); break;
            case HotkeySettings:  OpenSettings(); break;
        }
    }

    private void PowerStep(int watts)
    {
        _guard.SetErg((_guard.TargetPowerW ?? 150) + watts);
        Render(_state.Current);                                             // sofort zeigen, nicht erst im nächsten Takt
    }

    // Zurück in die Simulation, mit der Steigung, die gerade am Trainer anliegen soll.
    private void EndErg()
    {
        _guard.EndErg(_sim.Route is null ? 0 : _limiter.Current);
        Render(_state.Current);
    }

    private void PowerUp_Click(object sender, RoutedEventArgs e) => PowerStep(+10);

    private void PowerDown_Click(object sender, RoutedEventArgs e) => PowerStep(-10);

    private void ErgToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_guard.Mode == ControlMode.Erg) EndErg();
        else PowerStep(0);
    }

    private void EndErg_Click(object sender, RoutedEventArgs e) => EndErg();

    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        Native.SetClickThrough(this, on);
        ClickThroughButton.IsChecked = on;
        if (on) ShowToast(Mood.Idle, "Durchklicken an", "Klicks gehen jetzt ans Video. Strg+Alt+D schaltet zurück.");
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e) => SetClickThrough(ClickThroughButton.IsChecked == true);

    // Beenden über das Menü: erst zuklappen, dann schließen
    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting) return;
        _exiting = true;
        PlayClose(Close);
    }

    // ---------- Avatar: Maus darüber und Antippen ----------

    private void Mascot_MouseEnter(object sender, MouseEventArgs e) => Mascot.Hover(true);

    private void Mascot_MouseLeave(object sender, MouseEventArgs e) => Mascot.Hover(false);

    // Antippen: Er staucht sich und federt. Fünf Stupser in kurzer Zeit sind zu viel (Sheet 9 und 10):
    // Er verschwindet, meldet sich schwindelig und ist nach drei Sekunden wieder da.
    private void Mascot_Poke(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        long now = Environment.TickCount64;
        _pokes.RemoveAll(t => now - t > 2500);
        _pokes.Add(now);
        if (_pokes.Count < 5) { Mascot.Poke(); return; }
        _pokes.Clear();
        Motion.PopOut(Mascot, 0.3, 180);
        ShowToast(Mood.Dizzy, "Zu viele Stupser auf einmal.", "Gib mir eine Sekunde – in drei Sekunden geht's weiter.", 3);
        Motion.After(3300, () => Motion.PopIn(Mascot, 0.4, 480, 0.5, 1));
    }

    // ---------- Aufklappen und Zuklappen ----------

    // Aufklappen wie in Sheet 1: Aus der Notch wächst das Panel etwas über seine Größe hinaus und schnappt zurück.
    // Der Avatar ist zuerst da, Funken sammeln sich um ihn, dann erscheinen Kopfzeile und Inhalt nacheinander.
    // Zum Schluss freut er sich und winkt.
    private void PlayOpen()
    {
        Root.Opacity = 1;
        UpdateLayout();
        Sparkle(140);                                                       // misst vor dem Skalieren
        var shell = Motion.Scale(Shell);
        Shell.RenderTransformOrigin = new Point(0.5, _settings.Docked ? 0 : 0.5);
        Motion.Run(shell, ScaleTransform.ScaleXProperty, 0.3, 1, 540, Motion.Expand);
        Motion.Run(shell, ScaleTransform.ScaleYProperty, 0.22, 1, 580, Motion.Expand);
        Motion.Run(Shell, OpacityProperty, 0, 1, 140);
        Motion.PopIn(LeftEar, 0, 320, 1, 0, delayMs: 420);
        Motion.PopIn(RightEar, 0, 320, 0, 0, delayMs: 420);
        Motion.Run(Header, OpacityProperty, 0, 1, 260, delayMs: 240);
        Motion.Reveal([HomeTab, RouteTab, ErgTab], 260, 45);
        Motion.PopIn(Mascot, 0.5, 520, 0.5, 1, delayMs: 60);
        Motion.Reveal([PowerColumn, SideColumn, RouteLoaded, .. StatsGrid.Children.Cast<UIElement>()], 400, 60);
        Mascot.Greet(520);
    }

    // Zuklappen wie in Sheet 3: Inhalt blendet aus, der Avatar schließt die Augen, das Panel holt kurz Schwung
    // und zieht sich in die Notch zurück
    private void PlayClose(Action done)
    {
        Mascot.Flash(Mood.Content, 2000);
        foreach (UIElement part in new UIElement[] { Header, PowerColumn, SideColumn, RouteLoaded, StatsGrid, RouteView, ErgView, Toast, BluetoothCard })
            Motion.Run(part, OpacityProperty, part.Opacity, 0, 140);
        Motion.PopOut(LeftEar, 0, 140);
        Motion.PopOut(RightEar, 0, 140);
        var shell = Motion.Scale(Shell);
        Shell.RenderTransformOrigin = new Point(0.5, _settings.Docked ? 0 : 0.5);
        Motion.Run(shell, ScaleTransform.ScaleXProperty, shell.ScaleX, 0.3, 380, Motion.Anticipate, 120);
        Motion.Run(shell, ScaleTransform.ScaleYProperty, shell.ScaleY, 0.22, 380, Motion.Anticipate, 120);
        Motion.Run(Shell, OpacityProperty, 1, 0, 160, Motion.In, 360, done);
    }

    // Funken wie in Sheet 1: verstreut über das Panel, sie ziehen sich zu einem Kranz um den Avatar zusammen und vergehen
    private void Sparkle(int delayMs)
    {
        if (!Motion.Enabled || Fx.ActualWidth <= 0) return;
        var center = Mascot.TranslatePoint(new Point(Mascot.ActualWidth / 2, Mascot.ActualHeight / 2), Fx);
        for (int i = 0; i < 26; i++)
        {
            double size = 1.5 + Rng.NextDouble() * 1.8;
            var dot = new System.Windows.Shapes.Ellipse { Width = size, Height = size, Opacity = 0 };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            var shift = Motion.Shift(dot);
            Fx.Children.Add(dot);
            double angle = Rng.NextDouble() * 2 * Math.PI, radius = 40 + Rng.NextDouble() * 16;
            int start = delayMs + Rng.Next(140);
            Motion.Run(shift, TranslateTransform.XProperty, Rng.NextDouble() * Fx.ActualWidth, center.X + Math.Cos(angle) * radius * 1.3, 760, Motion.Out, start);
            Motion.Run(shift, TranslateTransform.YProperty, Rng.NextDouble() * Fx.ActualHeight, center.Y + Math.Sin(angle) * radius * 0.85, 760, Motion.Out, start);
            Motion.Keys(dot, OpacityProperty, start, [(0, 0), (160, 0.9), (620, 0.75), (980, 0)], done: () => Fx.Children.Remove(dot));
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _timer.Stop();                                                      // keine neue Aufzeichnung nach dem Export
        var hwnd = Native.Handle(this);
        for (int id = 1; id <= 10; id++) Native.UnregisterHotKey(hwnd, id);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        try { _ride.Export(); }
        catch (Exception ex) { RawLog.Note("Export beim Beenden fehlgeschlagen: " + ex.Message); }   // die CSV bleibt, Rettung beim nächsten Start
        _settingsWindow?.Close();
        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_trainer is not null) await _trainer.DisposeAsync();            // sonst bleibt der Trainer für andere Apps belegt
        if (_strap is not null) await _strap.DisposeAsync();
        base.OnClosed(e);
    }
}
