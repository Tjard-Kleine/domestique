using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private const double HudW = 401, MapW = 328;              // Notch wie im Entwurf; ausgeklappt kommt die Karte (8 + 320) dazu
    private const double BaseWidth = HudW + MapW + 28 + 16;   // + 2 × 14 Notch-Ohren + Platz zum Nachfedern der Breite
    private const double SnapDistance = 28, CenterSnap = 90;  // Andocken am oberen Rand, Einrasten in der Mitte
    private const double Overhang = 12;                       // durchsichtiger Rand unten (Root.Margin): Platz zum Nachfedern
    private const double TabStep = 36;                        // Breite eines Tabs in der Kopfzeile samt Abstand
    private const double StartW = 288;                        // kleine Pille, aus der die Notch beim Start aufgeht (wie Coucou kompakt)
    private const double CompactH = 36, CompactR = 14;        // minimierte Notch; ihre Breite hängt an den gewählten Werten
    private const double NotchW = 184;                        // so schmal zieht sie sich beim Beenden in den Rand zurück
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
    private readonly DispatcherTimer _noticeTimer = new();
    private (NoticeKind Kind, string Text, double Seconds)? _pendingNotice;     // Hinweis, der auf das Ende des Starts wartet
    private long _deviceSeen;                                                    // zuletzt ein Gerät verbunden (Trainer oder Pulsgurt)
    private bool _asleep = true, _wasRecording;
    private readonly ModeGuard _guard;
    private readonly RouteSim _sim = new(new RiderPhysics());
    private readonly GradeLimiter _limiter = new();
    private readonly RideRecorder _ride = new(RidesFolder);
    private readonly Dictionary<HotkeyAction, bool> _hotkeyOk = [];      // false: eine andere App hat das Kürzel schon
    private readonly List<long> _pokes = new();                           // Zeitpunkte der letzten Klapse auf den Avatar
    private readonly Island _island;
    private readonly Tracked _shellWidth = new(HudW);                     // Breite beim Aus- und Einklappen der Karte
    private readonly FrameLoop _widthLoop;
    private bool _greeting, _compact, _anchorTop, _switching, _mapOpen, _riding;
    private Color _surface;                                        // Hintergrund mit eingestellter Deckkraft
    private bool _opaque = true;                                   // ganz deckend, solange kein Trainer verbunden ist
    private BleTrainer? _trainer;
    private BleHeartRate? _strap;
    private SettingsWindow? _settingsWindow;
    private Func<RoutePoint, Point>? _mapProject;
    private bool _clickThrough, _closing, _trainerLoop, _strapLoop, _connectingTrainer, _connectingStrap;
    private bool _wasConnected, _everConnected, _hadControl, _controlLost, _strapWas, _wasFinished, _dockCentered;
    private string _message = "Searching for trainer …";
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
        BoltIcon.Data = MiniBoltIcon.Data = Icons.Bolt;
        HeartIcon.Data = MiniHeartIcon.Data = Icons.HeartPulse;
        CadenceIcon.Data = MiniCadenceIcon.Data = Icons.Crank;
        SpeedIcon.Data = MiniSpeedIcon.Data = Icons.Speed;
        DistanceIcon.Data = MiniDistanceIcon.Data = Icons.Distance;
        ClimbIcon.Data = MiniClimbIcon.Data = Icons.Climb;
        TimeIcon.Data = MiniTimeIcon.Data = Icons.Clock;
        _widthLoop = new FrameLoop((dt, now) =>
        {
            _shellWidth.Step(dt, now);
            Shell.Width = _shellWidth.Value;
            return _shellWidth.Animating;
        });
        Ride.Landing += Ride_Landing;
        _guard = new ModeGuard(command => _trainer?.Control?.SetTarget(command));
        _timer.Tick += (_, _) => OnTick();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Motion.Run(NoticeBar, OpacityProperty, NoticeBar.Opacity, 0, 350, Co.InOutE); };
        _island = new Island(Shell, LeftEar, RightEar);
        Greeting.ChooseTarget = () => _bluetoothOff ? BluetoothAvatar : Mascot;
        Greeting.HeaderOpacity += o => Header.Opacity = o;
        Greeting.IslandBounds = () =>
        {
            var r = _island.VisibleRect;
            return new Rect(Shell.TranslatePoint(r.TopLeft, Greeting), r.Size);
        };
        Greeting.Landing += Greeting_Landing;
        Greeting.Finished += Greeting_Finished;
        SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged || e.PreviousSize.Height <= 0) return;
            if (_settings.Docked) Dock(center: _dockCentered);     // angedockt: oben bleiben
            else if (!_anchorTop) Top -= e.NewSize.Height - e.PreviousSize.Height;   // sonst bleibt der untere Rand stehen
        };
        MouseMove += (_, e) => ActiveMascot.LookAt(e.GetPosition(ActiveMascot));   // Augen folgen der Maus
        MouseLeave += (_, _) => ActiveMascot.LookAhead();
        // Die Streckenauswahl ist so hoch wie die Übersicht (die Liste scrollt), statt das Fenster zu vergrößern.
        // Die Übersicht ist oben ausgerichtet, ihre Höhe ist also ihre echte Höhe: Das Fenster schrumpft auch wieder.
        HomeView.SizeChanged += (_, _) => RouteView.Height = HomeView.ActualHeight;
    }

    public string TrainerStatus => _trainer is { } t && _state.Current.Connected ? $"{t.Name} · connected" : _message;
    public bool BluetoothOff => _bluetoothOff;                     // dieselbe Erkennung wie unter „Domestique“ im Overlay

    // Trainer und Pulsgurt zählen je einmal, die Steuerung läuft über den Trainer
    public int ConnectedDevices =>
        ((_trainer is not null || Demo) && _state.Current.Connected ? 1 : 0) + (_strap is not null && _state.Current.StrapConnected ? 1 : 0);

    public bool IsConnected(SavedDevice d) => d.Kind == "Trainer"
        ? _trainer is not null && _state.Current.Connected && _settings.TrainerAddress == d.Address
        : _strap is not null && _state.Current.StrapConnected && _settings.StrapAddress == d.Address;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(Native.Handle(this)).AddHook(WndProc);
        ApplyHotkeys();
    }

    // ---------- Tastenkürzel ----------

    // Meldet alle Kürzel (neu) bei Windows an. Belegt eine andere App eins, bleibt es aus und die Einstellungen sagen es.
    public void ApplyHotkeys()
    {
        var hwnd = Native.Handle(this);
        if (hwnd == IntPtr.Zero) return;
        foreach (var (action, _, _) in Hotkeys.All)
        {
            Native.UnregisterHotKey(hwnd, (int)action);
            if (Hotkeys.Get(_settings, action) is not { } combo) { _hotkeyOk[action] = true; continue; }
            bool ok = Native.RegisterHotKey(hwnd, (int)action, combo.NativeModifiers | Hotkeys.Repeat(action), combo.VirtualKey);
            if (!ok && _hotkeyOk.GetValueOrDefault(action, true)) RawLog.Note($"Hotkey {combo} is used by another app");
            _hotkeyOk[action] = ok;
        }
        UpdateShortcutTexts();
    }

    // Während man in den Einstellungen ein neues Kürzel drückt: alle kurz abmelden, sonst löst die Taste die Aktion aus
    public void SuspendHotkeys()
    {
        var hwnd = Native.Handle(this);
        foreach (var (action, _, _) in Hotkeys.All) Native.UnregisterHotKey(hwnd, (int)action);
    }

    public bool HotkeyWorks(HotkeyAction action) => Hotkeys.Get(_settings, action) is not null && _hotkeyOk.GetValueOrDefault(action, true);

    // „Settings“ → „Settings (Ctrl+Alt+S)“, ohne Kürzel nur der Name
    private string WithKeys(string text, HotkeyAction action) =>
        HotkeyWorks(action) ? $"{text} ({Hotkeys.Text(_settings, action)})" : text;

    // Tooltips nennen die aktuellen Kürzel
    private void UpdateShortcutTexts()
    {
        ClickThroughButton.ToolTip = WithKeys("Click-through: clicks go to the video", HotkeyAction.ClickThrough);
        SettingsButton.ToolTip = WithKeys("Settings", HotkeyAction.Settings);
        MinimizeButton.ToolTip = WithKeys("Minimize", HotkeyAction.Compact);
        ExpandButton.ToolTip = WithKeys("Expand", HotkeyAction.Compact);
        MapOpener.ToolTip = WithKeys("Show map", HotkeyAction.Map);
        MapCloser.ToolTip = WithKeys("Hide map", HotkeyAction.Map);
        var lines = new[] { HotkeyAction.ErgUp, HotkeyAction.ErgDown, HotkeyAction.ErgToggle }
            .Where(HotkeyWorks).Select(a => $"{Hotkeys.Text(_settings, a)}   {Hotkeys.Label(a)}").ToArray();
        ErgPill.ToolTip = lines.Length > 0 ? string.Join("\n", lines) : "ERG: set shortcuts in Settings";
    }

    // Kontextmenü: ERG starten oder beenden, je nach Zustand, mit den aktuellen Kürzeln
    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        string erg = _guard.Mode == ControlMode.Erg ? "End ERG" : "Start ERG";
        ErgMenuItem.Header = HotkeyWorks(HotkeyAction.ErgToggle) ? $"{erg}   {Hotkeys.Text(_settings, HotkeyAction.ErgToggle)}" : erg;
        SettingsMenuItem.Header = HotkeyWorks(HotkeyAction.Settings) ? $"Settings   {Hotkeys.Text(_settings, HotkeyAction.Settings)}" : "Settings";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RawLog.Note($"Domestique started {DateTime.Now:yyyy-MM-dd}");
        ApplySettings();                                                   // Stil und Größe vor dem Positionieren
        if (_settings.Docked && _settings.NotchDock && _settings.Left is double dockLeft) { Left = dockLeft; Dock(center: IsCentered()); }
        else if (_settings.Left is double left && _settings.Top is double top && IsOnScreen(left, top)) { Left = left; Top = top; SetDocked(false); }
        else PlaceDefault();
        // Erst nach dem ersten gerenderten Bild starten: Das erste Bild dauert beim Start spürbar (JIT, Grafik),
        // die Animation wäre sonst schon vorbei, bevor man etwas sieht. Bis dahin bleibt das Fenster unsichtbar.
        Root.Opacity = 0;
        _greeting = true;                                                  // Meldungen und Bluetooth-Karte warten
        ContentRendered += (_, _) => StartGreeting();
        if (!Demo)
        {
            _radio = await BluetoothRadio.WatchAsync(on => Dispatcher.BeginInvoke(() => OnBluetoothChanged(on)));
            if (_radio is { State: not Windows.Devices.Radios.RadioState.On }) OnBluetoothChanged(false);   // schon beim Start aus
        }
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);   // Review #9
        Native.KeepTopmost(this);                                           // Review #7
        if (RideRecorder.RecoverUnexported(RidesFolder).Count > 0) Notice(NoticeKind.Success, "Last ride recovered");
        Render(_state.Current);
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
        if (Demo) StartDemo();                                             // simulierter Trainer statt Bluetooth
        else _ = KeepTrainerConnectedAsync();
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
            _message = "Searching for trainer …";
            ulong? address = _settings.TrainerAddress
                ?? await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            _bluetoothOff = false;                                          // die Suche lief, also ist Bluetooth an
            if (address is null) { _message = "No trainer found"; return false; }
            _message = "Connecting …";
            await trainer.ConnectAsync(address.Value);
            _trainer = trainer;
            _lastTrainerError = null;
            _settings.TrainerAddress = address;
            _settings.Remember(address.Value, trainer.Name, "Trainer");   // erscheint unter „Meine Geräte“
            _settings.Save();
            if (trainer.PowerRange is { } range) _guard.PowerLimits = range;
            _guard.Reapply();                                               // Startzustand flache Straße oder das schon gewählte Ziel
            _message = "Waiting for trainer …";                               // erscheint nur, falls die Verbindung später abreißt
            return true;
        }
        catch (Exception ex)
        {
            await trainer.DisposeAsync();                                   // halbe Verbindung freigeben
            if (ex.Message != _lastTrainerError) RawLog.Note("Trainer: " + ex.Message);   // bei Dauerfehlern nicht alle 10 s
            _lastTrainerError = ex.Message;
            _bluetoothOff = ex.Message.Contains("Bluetooth");
            _message = _bluetoothOff ? "Bluetooth is off" : "Connection failed";
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
            if (ex.Message != _lastStrapError) RawLog.Note("HR strap: " + ex.Message);
            _lastStrapError = ex.Message;
            return false;
        }
        finally { _connectingStrap = false; }
    }

    public async Task<bool> ReconnectTrainerAsync()
    {
        if (_trainer is not null) { await _trainer.DisposeAsync(); _trainer = null; }
        _message = "Reconnecting …";
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
        if (!Demo) Record(_state.Current, stale);                         // Demo-Fahrten werden nicht gespeichert

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
        if (!_greeting) SetOpaque(!s.Connected);
        // schläft, solange kein Gerät verbunden ist; erst nach 5 s ohne Gerät, damit kurze Aussetzer ihn nicht wecken und hinlegen
        if (s.Connected || s.StrapConnected) _deviceSeen = Environment.TickCount64;
        _asleep = _deviceSeen == 0 || Environment.TickCount64 - _deviceSeen > 5000;

        // Kopfzeile: Verbindungspunkt und Anzahl verbundener Geräte, immer sichtbar. Ändert sich etwas, springt der Punkt kurz.
        if (SetFill(LinkDot, !s.Connected ? "MutedBrush" : stale || controlLost ? "WarningBrush" : "PositiveBrush")) Motion.PopIn(LinkDot, 0.3, 380);
        string count = ConnectedDevices.ToString();
        if (LinkCount.Text != count) { LinkCount.Text = count; Motion.Bounce(LinkButton, 0.85); }
        LinkButton.ToolTip = $"{TrainerStatus}\n{(s.StrapConnected ? "HR strap connected" : "No HR strap")}\nClick: Bluetooth settings";

        UpdateBluetoothCard();
        // nach dem gewählten Tab, nicht nach der Sichtbarkeit: Beim Wechsel blendet die alte Ansicht noch kurz aus
        if (_compact) RenderCompact(s, stale, controlLost, zone);
        else if (HomeTab.IsChecked == true) RenderHome(s, stale, controlLost, zone);
    }

    // Ohne Trainer, ohne Daten oder ohne Sensor steht 0 statt eines Strichs, auch bei Puls und Kadenz
    private static string PowerText(SessionSnapshot s, bool stale) => stale ? "0" : s.Power3sW.ToString();
    private static string HeartText(SessionSnapshot s) => (s.HeartRateBpm ?? 0).ToString();
    private static string CadenceText(SessionSnapshot s, bool stale) => stale || s.CadenceRpm is null ? "0" : $"{s.CadenceRpm:0}";

    // Gesicht: schläft ohne Gerät (grau), sonst nach FTP-Zone locker, konzentriert oder am Limit (Farbe der Zone).
    // Verbunden, aber gerade keine Daten (nicht getreten): normales Gesicht.
    private Mood MoodFor(SessionSnapshot s, bool stale, bool controlLost, int zone) =>
        _asleep ? Mood.Sleep
        : !s.Connected || stale ? Mood.Idle
        : zone >= 5 ? Mood.Strain
        : zone >= 3 ? Mood.Focus
        : Mood.Idle;

    // Abzeichen: „…“, solange er schläft und sucht (Bluetooth an), „!“, wenn eine andere App die Steuerung hat
    private void SetBadges(Avatar avatar, bool controlLost)
    {
        avatar.Busy = _asleep && !_bluetoothOff;
        avatar.Warning = controlLost;
    }

    private Avatar ActiveMascot => _compact ? MiniMascot : Mascot;

    private static Color? ZoneColor(int zone) => zone >= 2 ? ((SolidColorBrush)ZoneBrushes[zone]).Color : null;

    // Minimiert: Avatar und die gewählten Werte in einer Reihe
    private void RenderCompact(SessionSnapshot s, bool stale, bool controlLost, int zone)
    {
        MiniMascot.Mood = MoodFor(s, stale, controlLost, zone);
        SetBadges(MiniMascot, controlLost);
        FillCompact(s, stale);
        if ((MiniMascot.Tag as int?) == zone) return;
        MiniMascot.Tag = zone;
        MiniMascot.Tint(ZoneColor(zone));
    }

    private void RenderHome(SessionSnapshot s, bool stale, bool controlLost, int zone)
    {
        Mascot.Mood = MoodFor(s, stale, controlLost, zone);
        SetBadges(Mascot, controlLost);

        // die drei wichtigsten Werte, immer sichtbar; „–“, wenn der Trainer sie nicht liefert (D100: keine Trittfrequenz).
        // Live-Werte springen wie bei Zwift: Ein Hochzählen bei jedem Wert würde das Overlay ständig neu zeichnen (CPU).
        PowerRun.Text = PowerText(s, stale);
        CadenceRun.Text = CadenceText(s, stale);
        HeartRun.Text = HeartText(s);
        // ERG wie im Entwurf: an mit Ziel in Watt, sonst nur „ERG OFF“. Beim Wechsel springt die Pille kurz auf.
        string erg = _guard.Mode == ControlMode.Erg && _guard.TargetPowerW is int target ? $"ERG ON: {target}W" : "ERG OFF";
        if (ErgPillText.Text != erg)
        {
            bool modeChanged = ErgPillText.Text.StartsWith("ERG ON") != erg.StartsWith("ERG ON");
            ErgPillText.Text = erg;
            if (modeChanged) Motion.PopIn(ErgPill, 0.8, 420, 0, 0.5);
        }
        ShowZone(zone);

        // Werte oben wie im Entwurf: Tempo ganzzahlig, Strecke mit einer Nachkommastelle, Zeit ohne Stunden, solange es geht
        SpeedRun.Text = $"{s.VirtualSpeedKmh:0}";
        DistanceRun.Text = $"{s.DistanceM / 1000:0.0}";
        ClimbRun.Text = $"{_sim.ClimbedM:0}";
        TimeRun.Text = Elapsed(_sim.MovingTime);                            // zählt nur, solange Watt anliegen
        RenderRoute();
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

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
            segment.Opacity = i + 1 == zone ? 1 : 0.3;                     // blasse Töne wie im Entwurf
            if (i + 1 != zone) continue;
            segment.RenderTransformOrigin = new Point(0.5, 0.5);
            Motion.Run(Motion.Scale(segment), ScaleTransform.ScaleYProperty, 1.9, 1, 420, Motion.Pop);
        }
        Mascot.Tint(ZoneColor(zone));
    }

    // Strecke: Fortschrittsbalken im HUD, in der Kartenkarte gefahrene und ganze Länge
    private void RenderRoute()
    {
        if (_sim.Route is not { } r) { ProgressFill.Width = 0; return; }
        double done = Math.Min(_sim.RouteDistanceM, r.LengthM);
        double fill = Math.Round(done / r.LengthM * ((FrameworkElement)ProgressFill.Parent).ActualWidth, 1);
        if (ProgressFill.Width != fill) ProgressFill.Width = fill;
        RouteDistanceText.Text = $"{done / 1000:0.0} / {r.LengthM / 1000:0.0} KM";
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
        _message = on ? "Searching for trainer …" : "Bluetooth is off";
        if (!on) { _bluetoothCardDismissed = false; Render(_state.Current); return; }
        _wake.TrySetResult();                                              // Trainersuche wartet nicht die 10 s ab
        if (!_strapLoop && !Demo) _ = KeepStrapConnectedAsync();
        Render(_state.Current);
    }

    // „Bluetooth ist aus“ ist ein eigener Bildschirm, keine Karte darüber: Die Ansicht darunter ist weg, die Tabs sind
    // gesperrt. Der Avatar gleitet aus der Übersicht nach links in die Karte und beim Einschalten wieder zurück.
    // Die Karte bleibt, bis man tippt oder Bluetooth einschaltet.
    private void UpdateBluetoothCard()
    {
        bool show = _bluetoothOff && !_bluetoothCardDismissed;
        if (show == _bluetoothCardShown || _greeting) return;               // beim Start gleitet der Avatar selbst hinein
        bool fly = !_compact && HomeTab.IsChecked == true && !Greeting.Running;
        if (show) ShowBluetoothScreen(fly);
        else HideBluetoothScreen(fly);
    }

    private void ShowBluetoothScreen(bool fly)
    {
        _bluetoothCardShown = true;
        SetViewsLocked(true);
        BluetoothAvatar.Mood = Mood.Sleep;                                  // ohne Bluetooth schläft er (grau)
        BluetoothCard.Visibility = Visibility.Visible;
        Motion.Run(BluetoothCard, OpacityProperty, 0, 1, 320, Co.OutE);
        // Text erst, wenn der Avatar fast da ist: Beim Start kommt er aus der Mitte und würde sonst durch den Text gleiten
        Motion.Reveal(BluetoothText.Children.Cast<UIElement>(), fly ? 300 : 420, 80, rise: true);
        Glow(BluetoothGlow);
        var view = CurrentView;
        if (!fly) { view.Visibility = Visibility.Hidden; return; }          // beim Start: gleich der Bluetooth-Bildschirm
        Greeting.Fly(Mascot, BluetoothAvatar, 560);
        // Ansicht erst nach dem Abflug verstecken: Der Avatar startet an seinem Platz in der Übersicht
        Motion.Run(view, OpacityProperty, view.Opacity, 0, 260, Co.OutE, done: () =>
        {
            if (!_bluetoothCardShown) return;
            view.Visibility = Visibility.Hidden;
            view.Opacity = 1;
            Mascot.Opacity = 1;
        });
    }

    private void HideBluetoothScreen(bool fly)
    {
        _bluetoothCardShown = false;
        SetViewsLocked(false);
        var view = CurrentView;
        view.Visibility = Visibility.Visible;
        Motion.Run(view, OpacityProperty, 0, 1, 320, Co.OutE, 120);
        if (view == HomeView) Motion.Reveal(HudParts, 160, 55);
        if (fly) Greeting.Fly(BluetoothAvatar, Mascot, 560);
        Motion.Run(BluetoothCard, OpacityProperty, BluetoothCard.Opacity, 0, 300, Co.OutE, done: () =>
        {
            if (_bluetoothCardShown) return;
            BluetoothCard.Visibility = Visibility.Collapsed;
            BluetoothCard.Opacity = 1;
            BluetoothAvatar.Opacity = 1;
        });
        Render(_state.Current);
    }

    // Tabs gesperrt, solange der Bluetooth-Bildschirm steht
    private void SetViewsLocked(bool locked)
    {
        HomeTab.IsEnabled = RouteTab.IsEnabled = !locked;
        Motion.Run(TabBar, OpacityProperty, TabBar.Opacity, locked ? 0.35 : 1, 200, Co.OutE);
    }

    // Tippen öffnet die Bluetooth-Seite der Windows-Einstellungen
    private void BluetoothCard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        _bluetoothCardDismissed = true;
        UpdateBluetoothCard();
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception ex) { RawLog.Note("Couldn't open Bluetooth settings: " + ex.Message); }
    }

    // ---------- Meldungen ----------

    // Statt Meldungen über dem Inhalt: Jedes neu verbundene Gerät zeigt den grünen Haken am Avatar.
    // Was kein Symbol zeigen kann (Verbindung mitten in der Fahrt weg, Steuerung verloren), steht kurz in der Kopfzeile.
    private void DetectEvents(SessionSnapshot s, bool controlLost)
    {
        if (s.Connected != _wasConnected)
        {
            if (s.Connected) CheckMark();
            else if (_everConnected && !_closing) Notice(NoticeKind.Warning, "Trainer disconnected", 6);
            _everConnected |= s.Connected;
            _wasConnected = s.Connected;
        }
        bool hasControl = s.Connected && _trainer?.Control is { HasControl: true };
        if (controlLost && _hadControl) { _controlLost = true; Notice(NoticeKind.Warning, "Another app took control", 6); }
        if (hasControl && _controlLost) { _controlLost = false; CheckMark(); }
        _hadControl = hasControl;
        if (s.StrapConnected && !_strapWas) CheckMark();
        _strapWas = s.StrapConnected;
        // Aufnahme startet von selbst mit dem ersten Tritt: einmal kurz sagen, kleiner Hüpfer
        if (Recording && !_wasRecording) { Notice(NoticeKind.Info, "Recording started", 4); ActiveMascot.Hop(); }
        _wasRecording = Recording;
        if (_sim.Finished && !_wasFinished && _sim.Route is not null) { CheckMark(); Mascot.Greet(200); Notice(NoticeKind.Success, "Route finished"); }
        _wasFinished = _sim.Finished;
    }

    // grüner Haken an beiden Avataren (Übersicht und minimiert), je nachdem, welcher gerade zu sehen ist
    private void CheckMark()
    {
        Mascot.Done();
        MiniMascot.Done();
    }

    private enum NoticeKind { Info, Success, Warning, Error }

    // Kurzer Hinweis mittig in der Kopfzeile: verdeckt nichts und verschwindet von selbst. Während des Starts wartet er.
    private void Notice(NoticeKind kind, string text, double seconds = 5)
    {
        if (_greeting) { _pendingNotice = (kind, text, seconds); return; }
        NoticeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, kind switch
        {
            NoticeKind.Success => "PositiveBrush",
            NoticeKind.Warning => "WarningBrush",
            NoticeKind.Error => "NegativeBrush",
            _ => "AccentBrush",
        });
        NoticeText.Text = text;
        Motion.Rise(NoticeBar, 6, 380);
        _noticeTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(seconds);
        _noticeTimer.Start();
    }

    // Schein am unteren Rand der Bluetooth-Karte
    private static void Glow(System.Windows.Shapes.Shape glow)
    {
        glow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "NegativeBrush");
        Motion.Run(glow, OpacityProperty, 0, 0.5, 600);
    }
    // ---------- Ansichten ----------

    // Hidden statt Collapsed: Die verborgenen Ansichten behalten ihren Platz, so springt die Fenstergröße nicht.
    // Wechsel wie in Sheet 3 und 7: Der alte Inhalt dimmt kurz ab, der neue erscheint Teil für Teil, wächst etwas
    // über seine Größe hinaus und schnappt zurück. Die Auswahl gleitet zum neuen Tab.
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomeView is null || RouteView is null) return;                 // während InitializeComponent
        FrameworkElement next = CurrentView;
        foreach (var view in new FrameworkElement[] { HomeView, RouteView })
        {
            if (view == next || view.Visibility != Visibility.Visible) continue;
            Motion.Run(view, OpacityProperty, view.Opacity, 0, 120, done: () =>
            {
                if (view == CurrentView) return;                           // inzwischen wieder gewählt
                view.Visibility = Visibility.Hidden;
                view.Opacity = 1;
            });
        }
        // Bluetooth-Bildschirm steht (z. B. „Strecke laden …“ aus dem Menü) oder der Übergang nach dem Laden einer Strecke
        // läuft: Ansicht nur merken, sie kommt danach
        bool wait = _bluetoothCardShown || (_riding && next == HomeView);
        next.Visibility = wait ? Visibility.Hidden : Visibility.Visible;
        if (!wait) Motion.Run(next, OpacityProperty, next.Opacity, 1, 120);
        if (RouteTab.IsChecked == true) RefreshRouteList();
        Render(_state.Current);
        MoveDots();
        if (!wait) Motion.Reveal(RevealParts(next), 60, 55);
        if (next == RouteView) RevealRouteItems(160);

        var pill = Motion.Shift(TabPill);
        double x = (HomeTab.IsChecked == true ? 0 : 1) * TabStep;
        Motion.Run(pill, TranslateTransform.XProperty, pill.X, x, 380, Motion.Glide);
        TabPill.RenderTransformOrigin = new Point(0.5, 0.5);
        Motion.Keys(Motion.Scale(TabPill), ScaleTransform.ScaleXProperty, 0, [(0, 1), (110, 1.3), (400, 1)], Motion.Pop);   // streckt sich unterwegs
    }

    private FrameworkElement CurrentView => RouteTab.IsChecked == true ? RouteView : HomeView;

    // Teile einer Ansicht in der Reihenfolge, in der sie erscheinen
    private IEnumerable<UIElement> RevealParts(FrameworkElement view)
    {
        if (view == HomeView)
        {
            yield return RideCard;
            yield return MapCard;
        }
        else
        {
            yield return RouteBar;
            yield return AddPanel;
        }
    }

    // Werte der Übersicht ohne den Avatar und seine Karte: Landet er gerade, darf sich sein Platz nicht bewegen
    private UIElement[] HudParts => [.. TopStats.Children.Cast<UIElement>(), PowerColumn, PowerUnit, SideColumn, ErgPill, MapCard];

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
            if (_ride.Export() is { } file) Notice(NoticeKind.Success, "Ride saved · Documents\\Domestique", 6);
            else Notice(NoticeKind.Info, "No ride yet");
        }
        catch (Exception ex) { Notice(NoticeKind.Error, "Export failed: " + ex.Message, 8); }
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
        var items = new List<RouteItem> { new(null, "No route", NoRouteIcon, "flat") };
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
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX route (*.gpx)|*.gpx" };
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
        catch (Exception ex) { Notice(NoticeKind.Error, "Can't read file: " + ex.Message, 8); }
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
        catch (Exception ex) { Notice(NoticeKind.Error, "Can't read route: " + ex.Message, 8); }
    }

    // Die Strecke erscheint auf der Übersicht, mit dem Übergang dorthin
    private void ShowRoute(Route route, string key)
    {
        bool fromHome = HomeTab.IsChecked == true && HomeView.Visibility == Visibility.Visible;
        _sim.Load(route);
        _activeRoute = key;
        MapLine.Points = RouteGeometry.Map(route, 102, 102, out _mapProject);
        Profile.SetRoute(route);
        RouteNameText.Text = route.Name.ToUpperInvariant();
        PlayRouteLoaded(fromHome);
        Render(_state.Current);
        MoveDots();
        RawLog.Note($"Route loaded: {route.Name}, {route.LengthM / 1000:0.0} km");
    }

    private void UnloadRoute()
    {
        _sim.Unload();
        _activeRoute = null;
        _mapProject = null;
        Profile.SetRoute(null);
        SetMapOpen(false);
        HomeTab.IsChecked = true;
        Render(_state.Current);
    }

    private void MoveDots()
    {
        if (_sim.Route is not { } route || _mapProject is null || MapCard.Visibility != Visibility.Visible) return;
        double distance = Math.Min(_sim.RouteDistanceM, route.LengthM);
        var m = _mapProject(route.At(distance));
        Canvas.SetLeft(MapDot, m.X - 4);
        Canvas.SetTop(MapDot, m.Y - 4);
        MapDone.Points = RouteGeometry.Done(MapLine.Points, route, distance, _mapProject);   // gefahrener Teil markiert
        Profile.SetPosition(distance, _sim.GradePercent);
    }

    // ---------- Karte aus- und einklappen ----------

    private double ExpandedWidth => HudW + (_mapOpen ? MapW : 0);

    // Die Notch wächst mit Feder nach beiden Seiten (bleibt also mittig), die Karte blendet dabei ein.
    // Beim Einklappen blendet sie erst aus, dann zieht sich die Notch ohne Nachfedern zusammen.
    private void SetMapOpen(bool open, bool animate = true)
    {
        open &= _sim.Route is not null;
        MapOpener.Visibility = _sim.Route is not null && !open ? Visibility.Visible : Visibility.Collapsed;
        if (open == _mapOpen) return;
        _mapOpen = open;
        if (_compact || _switching || _greeting || !animate || !Motion.Enabled)
        {
            MapCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            MapCard.Opacity = 1;
            if (!_compact) { _widthLoop.Stop(); Shell.Width = ExpandedWidth; }   // minimiert: Aufklappen setzt die Breite
            MoveDots();
            return;
        }
        if (open)
        {
            MapCard.Visibility = Visibility.Visible;
            MoveDots();
            Motion.Run(MapCard, OpacityProperty, 0, 1, 260, Co.OutE, 120);
            AnimateWidth(ExpandedWidth, 0.86);
        }
        else
        {
            Motion.Run(MapCard, OpacityProperty, MapCard.Opacity, 0, 140, Co.OutE, done: () =>
            {
                if (_mapOpen) return;                                       // inzwischen wieder ausgeklappt
                MapCard.Visibility = Visibility.Collapsed;
                MapCard.Opacity = 1;
            });
            Motion.After(80, () => { if (!_mapOpen) AnimateWidth(ExpandedWidth, 1); });
        }
    }

    private void AnimateWidth(double target, double damping)
    {
        if (!_shellWidth.Animating) _shellWidth.Jump(Shell.Width);
        _shellWidth.SpringTo(target, 0.42, damping);
        _widthLoop.Start();
    }

    // kein Knopf: Der rechte Rand leuchtet beim Darüberfahren leicht auf, ein Klick klappt die Karte aus oder ein
    private void MapEdge_MouseEnter(object sender, MouseEventArgs e) => LightEdge((Border)sender, 1);

    private void MapEdge_MouseLeave(object sender, MouseEventArgs e) => LightEdge((Border)sender, 0);

    private void MapEdge_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        LightEdge((Border)sender, 0);
        _settings.ShowMap = !_mapOpen;                                      // gilt auch für die nächste Strecke
        SetMapOpen(!_mapOpen);
    }

    private static void LightEdge(Border edge, double to)
    {
        var glow = (UIElement)edge.Child;
        Motion.Run(glow, OpacityProperty, glow.Opacity, to, to > 0 ? 160 : 260, Co.OutE);
    }

    // ---------- Einstellungen (Phase 9) ----------

    public void ApplySettings()
    {
        _settings.Theme = App.ApplyTheme(_settings.Theme);
        var surface = (Color)FindResource("SurfaceColor");
        double minOpacity = TryFindResource("MinOpacity") as double? ?? 0.2;                  // ganz durchsichtig wäre unlesbar
        surface.A = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, minOpacity, 1) * 255);
        _surface = surface;
        ApplyBackground();

        double scale = Math.Clamp(_settings.Scale, 0.5, 2);
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
        SetMapOpen(_settings.ShowMap);
        ApplyCompactValues();
        if (!_settings.NotchDock && _settings.Docked) { SetDocked(false); Top += 12; }
        else SetDocked(_settings.Docked);                                  // Ecken passend zum Stil

        _sim.Physics.MassKg = _settings.RiderKg + _settings.BikeKg;
        _sim.Physics.CdA = Math.Clamp(_settings.CdA, 0.1, 1);
        Render(_state.Current);
    }

    // Welche Werte die kleine Notch zeigt (Einstellungen). Ist sie gerade klein, passt sie ihre Breite weich an.
    private void ApplyCompactValues()
    {
        foreach (FrameworkElement slot in CompactValues.Children)
            slot.Visibility = _settings.CompactValues.Contains((string)slot.Tag) ? Visibility.Visible : Visibility.Collapsed;
        if (_compact && !_switching && Math.Abs(Shell.Width - CompactWidth) > 0.5) AnimateWidth(CompactWidth, 0.86);
    }

    private void FillCompact(SessionSnapshot s, bool stale)
    {
        MiniPowerRun.Text = PowerText(s, stale);
        MiniHeartRun.Text = HeartText(s);
        MiniCadenceRun.Text = CadenceText(s, stale);
        MiniSpeedRun.Text = $"{s.VirtualSpeedKmh:0}";
        MiniDistanceRun.Text = $"{s.DistanceM / 1000:0.0}";
        MiniClimbRun.Text = $"{_sim.ClimbedM:0}";
        MiniTimeRun.Text = Elapsed(_sim.MovingTime);
        FitCompactSlots();
    }

    // Strecke, Höhenmeter und Zeit wachsen nur: Ihr Feld ist so breit wie der Wert. Kommt eine Stelle dazu (selten),
    // wächst die kleine Notch weich mit. Ziffern sind gleich breit, die Breite ändert sich also nur mit der Stellenzahl.
    private void FitCompactSlots()
    {
        bool changed = false;
        foreach (var (slot, run, icon) in new[] { (DistanceSlot, MiniDistanceRun, MiniDistanceIcon), (ClimbSlot, MiniClimbRun, MiniClimbIcon),
                                                  (TimeSlot, MiniTimeRun, MiniTimeIcon) })
        {
            var text = new FormattedText(run.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(TryFindResource("HudFont") as FontFamily ?? FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                21, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double width = Math.Ceiling(text.WidthIncludingTrailingWhitespace + icon.Margin.Left + icon.Width);
            if (Math.Abs(slot.Width - width) < 0.5) continue;
            slot.Width = width;
            changed |= slot.Visibility == Visibility.Visible;
        }
        if (changed && _compact && !_switching) AnimateWidth(CompactWidth, 0.86);
    }

    // Breite der kleinen Notch: Rahmen, Rand, Avatar, die gezeigten Felder samt Abstand, Aufklappen
    private double CompactWidth =>
        2 + 8 + MiniMascot.Width + MiniMascot.Margin.Right
        + CompactValues.Children.OfType<FrameworkElement>().Where(s => s.Visibility == Visibility.Visible).Sum(s => s.Width + s.Margin.Right)
        + 32 + 4;

    // Hintergrund für Panel und Notch-Ohren: ganz deckend, bis ein Trainer verbunden ist, dann die eingestellte Deckkraft
    private Color ShellColor
    {
        get
        {
            var c = _surface;
            if (_opaque) c.A = 255;
            return c;
        }
    }

    private void ApplyBackground()
    {
        Shell.Background = LeftEar.Fill = RightEar.Fill = Frozen(ShellColor);
        var solid = _surface;
        solid.A = 255;
        NoticeBar.Background = Frozen(solid);                               // Hinweis deckt, was unter ihm in der Kopfzeile liegt
    }

    // weich überblendet, wenn sich der Verbindungszustand ändert
    private void SetOpaque(bool opaque)
    {
        if (_opaque == opaque) return;
        var from = ShellColor;
        _opaque = opaque;
        if (!Motion.Enabled) { ApplyBackground(); return; }
        var brush = new SolidColorBrush(from);
        Shell.Background = LeftEar.Fill = RightEar.Fill = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ShellColor, TimeSpan.FromMilliseconds(600)) { EasingFunction = Co.InOutE });
        Motion.After(650, () => { if (Shell.Background == brush) ApplyBackground(); });
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
        double shellRight = Shell.TranslatePoint(new Point(Shell.ActualWidth, 0), this).X;   // rechte Kante der Notch, nicht des Fensters
        double left = Math.Clamp(Left + shellRight - width, screen.Left + 8, screen.Right - width - 8);
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
        if (Greeting.Running) Greeting.Skip();                             // Klick beim Start: gleich auf den Platz
        if (_switching) return;
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

    // Andocken wie ein Tropfen, der mit dem Rand verschmilzt: gleitet weich hinauf (und in die Mitte), die oberen Ecken
    // werden flach und die Notch-Ohren fließen aus den Kanten heraus. Ohne Nachfedern.
    private void DockAnimated(bool center)
    {
        var screen = Native.MonitorBounds(this);
        double left = center ? screen.Left + (screen.Width - ActualWidth) / 2 : Left;
        bool wasDocked = _settings.Docked;
        _settings.Left = left;
        _settings.Top = screen.Top;
        if (center) Motion.Run(this, LeftProperty, Left, left, 260, Co.InOutE);
        Motion.Run(this, TopProperty, Top, screen.Top, 260, Co.InOutE, done: () =>
        {
            Dock(center);
            if (!wasDocked) Merge();
        });
    }

    private void Merge()
    {
        var r = (CornerRadius)FindResource("ShellRadius");
        LeftEar.RenderTransformOrigin = new Point(1, 0);                   // wachsen aus der Ecke am Panel
        RightEar.RenderTransformOrigin = new Point(0, 0);
        var shell = Motion.Scale(Shell);
        Shell.RenderTransformOrigin = new Point(0.5, 0);
        Tween.Run(420, Co.InOut, k =>
        {
            double top = r.TopLeft * (1 - k);
            Shell.CornerRadius = new CornerRadius(top, top, r.BottomRight, r.BottomLeft);
            double ear = Co.Out(k);
            Motion.Scale(LeftEar).ScaleX = Motion.Scale(LeftEar).ScaleY = ear;
            Motion.Scale(RightEar).ScaleX = Motion.Scale(RightEar).ScaleY = ear;
            shell.ScaleY = 1 + 0.025 * Math.Sin(Math.PI * k);                // zieht sich kurz zum Rand hin
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
        _island.Docked = docked;
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
        switch ((HotkeyAction)id)
        {
            case HotkeyAction.ErgUp: PowerStep(+10); break;
            case HotkeyAction.ErgDown: PowerStep(-10); break;
            case HotkeyAction.ErgToggle: ToggleErg(); break;
            case HotkeyAction.ClickThrough: SetClickThrough(!_clickThrough); break;
            case HotkeyAction.Compact: if (_compact) Expand_Click(this, new RoutedEventArgs()); else Minimize_Click(this, new RoutedEventArgs()); break;
            case HotkeyAction.Map: ToggleMap(); break;
            case HotkeyAction.Settings: OpenSettings(); break;
        }
    }

    // Karte per Tastenkürzel wie per Klick auf den Rand; ohne Strecke gibt es keine
    private void ToggleMap()
    {
        if (_sim.Route is null) { Notice(NoticeKind.Info, "No route loaded", 3); return; }
        _settings.ShowMap = !_mapOpen;
        SetMapOpen(!_mapOpen);
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

    // ERG an (mit dem letzten Ziel, sonst 150 W) oder aus
    private void ToggleErg()
    {
        if (_guard.Mode == ControlMode.Erg) EndErg();
        else PowerStep(0);
    }

    private void ErgToggle_Click(object sender, RoutedEventArgs e) => ToggleErg();

    // Durchklicken nur mit funktionierendem Kürzel: Klicks gehen dann durchs Overlay, das Kürzel ist der einzige Weg zurück
    private void SetClickThrough(bool on)
    {
        if (on && !HotkeyWorks(HotkeyAction.ClickThrough))
        {
            ClickThroughButton.IsChecked = false;
            Notice(NoticeKind.Warning, "Set a click-through shortcut first", 6);
            return;
        }
        _clickThrough = on;
        Native.SetClickThrough(this, on);
        ClickThroughButton.IsChecked = on;
        if (on) Notice(NoticeKind.Info, $"Click-through on · {Hotkeys.Text(_settings, HotkeyAction.ClickThrough)} to undo", 6);
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

    private void Mascot_MouseEnter(object sender, MouseEventArgs e) => ((Avatar)sender).Hover(true);

    private void Mascot_MouseLeave(object sender, MouseEventArgs e) => ((Avatar)sender).Hover(false);

    // Klaps wie in Coucou: Er staucht sich, federt und kneift die Augen zusammen. Drei Klapse in 1,7 s sind zu viel:
    // Er dreht sich schwindelig, ganz ohne eigenen Bildschirm, und ist danach wieder der Alte.
    private void Mascot_Poke(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        var avatar = (Avatar)sender;
        long now = Environment.TickCount64;
        _pokes.RemoveAll(t => now - t > 1700);
        _pokes.Add(now);
        avatar.Slap();
        if (_pokes.Count < 3) return;
        _pokes.Clear();
        avatar.Dizzy();
    }

    // ---------- Strecke geladen ----------

    // Übergang statt Ladeanimation, sofort beim Bestätigen: Die Streckenauswahl (oder die Übersicht) blendet aus, der
    // Avatar fährt über eine gerade Linie mit ein paar Steinchen und landet auf seinem Platz, während die Übersicht
    // erscheint. Danach klappt die Karte aus (falls eingestellt) und der grüne Haken kommt.
    // Minimiert, auf dem Bluetooth-Bildschirm oder ohne Animationseffekte: gleich die Übersicht.
    private void PlayRouteLoaded(bool fromHome)
    {
        void Finish()
        {
            SetMapOpen(_settings.ShowMap);
            CheckMark();
        }
        if (_compact || _bluetoothCardShown || _greeting || !Motion.Enabled || Ride.Running)
        {
            HomeTab.IsChecked = true;
            Finish();
            return;
        }
        _riding = true;
        if (fromHome) Motion.Run(HomeView, OpacityProperty, HomeView.Opacity, 0, 160, Co.OutE);
        HomeTab.IsChecked = true;                                           // die Auswahl gleitet schon, die Übersicht wartet
        UpdateLayout();
        var area = new Rect(ViewsArea.TranslatePoint(new Point(0, 0), Ride), new Size(ViewsArea.ActualWidth, ViewsArea.ActualHeight));
        Ride.Start(fromHome ? Mascot : null, Mascot, area, () =>
        {
            _riding = false;
            Finish();
        });
    }

    // Der Avatar gleitet auf seinen Platz: Übersicht einblenden, die Werte erscheinen nacheinander
    private void Ride_Landing()
    {
        _riding = false;
        if (CurrentView != HomeView || _bluetoothCardShown) return;          // inzwischen woanders
        HomeView.Visibility = Visibility.Visible;
        Motion.Run(HomeView, OpacityProperty, 0, 1, 300, Co.OutE);
        Motion.Reveal(HudParts, 60, 45);
        Render(_state.Current);
    }
    // ---------- Start, Minimieren, Beenden ----------

    // Start wie in Sheet 1 und 2 (Ablauf aus Coucou): Die kleine Notch geht mit Feder auf, in voller Deckkraft.
    // GreetingView zeichnet Karte, Partikel und den Avatar; am Ende gleitet er auf seinen Platz.
    private void StartGreeting()
    {
        Root.Opacity = 1;
        UpdateLayout();
        if (!Motion.Enabled) { Greeting_Finished(); return; }
        Views.Opacity = 0;
        Header.Opacity = 0;
        Mascot.Opacity = 0;
        ApplyBackground();                                                  // ohne Trainer in voller Deckkraft
        _island.Docked = _settings.Docked;
        _island.Jump(StartW, CompactH, CompactR);
        _island.Open();
        Greeting.Prepare();                                                 // erst angeordnet, dann messen
        var area = ViewsArea.TranslatePoint(new Point(0, 0), Greeting);
        Greeting.Start(new Rect(area, new Size(ViewsArea.ActualWidth, ViewsArea.ActualHeight)), StartW, CompactH);
    }

    // Der Avatar gleitet auf seinen Platz: in die Übersicht oder nach links in die Bluetooth-Karte.
    // Der Inhalt erscheint dabei nacheinander.
    private void Greeting_Landing(Avatar target)
    {
        target.Opacity = 0;
        Header.Opacity = 1;
        Motion.Run(Views, OpacityProperty, 0, 1, 260, Co.OutE);
        if (target == BluetoothAvatar) ShowBluetoothScreen(fly: false);
        else Motion.Reveal(HudParts, 140, 60);
    }

    private void Greeting_Finished()
    {
        _greeting = false;
        Views.Opacity = Header.Opacity = 1;
        Mascot.Opacity = BluetoothAvatar.Opacity = 1;                       // beide wieder sichtbar, egal wo er gelandet ist
        Render(_state.Current);                                             // Bluetooth kann sich inzwischen geändert haben
        if (_pendingNotice is { } n) { _pendingNotice = null; Notice(n.Kind, n.Text, n.Seconds); }
    }

    // Minimieren per Knopf oder Tastenkürzel (Sheet 3, obere Reihe): Inhalt blendet aus, die Insel zieht sich mit der
    // Schließkurve zur kleinen Notch zusammen, darin erscheinen Avatar und die gewählten Werte. Nie von selbst.
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_compact || _switching || _greeting) return;
        _switching = _anchorTop = true;
        _widthLoop.Stop();                                                  // Karte klappt gerade: Breite steht ab jetzt fest
        FillCompact(_state.Current, IsStale(_state.Current));               // Felder passend zu den Werten, bevor die Breite feststeht
        // Fenster behält seine volle Höhe (unter der Pille durchsichtig, Klicks gehen durch): Ändert ein durchsichtiges
        // Fenster seine Größe, zeigt Windows kurz das alte Bild in der neuen Größe, beim Aufklappen sah man das flackern
        MinHeight = ActualHeight;
        Motion.Run(Expanded, OpacityProperty, 1, 0, 110, Co.OutE);         // erst ausblenden, dann zusammenziehen,
        Motion.After(100, () => _island.Close(CompactWidth, CompactH, CompactR, () =>   // sonst schneidet die Kante den Inhalt
        {
            _compact = true;
            Expanded.Visibility = Visibility.Collapsed;
            Expanded.Opacity = 1;
            CompactBar.Visibility = Visibility.Visible;
            Shell.Width = CompactWidth;
            Shell.Padding = new Thickness(8, 0, 4, 0);
            UpdateLayout();
            _island.Reset();
            Render(_state.Current);
            Motion.PopIn(MiniMascot, 0.4, 420, 0.5, 0.6);
            Motion.Reveal([.. CompactValues.Children.Cast<UIElement>(), ExpandButton], 60, 35);
            _switching = false;
        }));
    }

    // Aufklappen: Die kleine Notch geht mit Feder (wie beim Start) wieder auf, der Inhalt erscheint nacheinander
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (!_compact || _switching) return;
        _switching = true;
        Motion.Run(CompactBar, OpacityProperty, 1, 0, 100, Co.OutE, done: () =>
        {
            _compact = false;
            CompactBar.Visibility = Visibility.Collapsed;
            CompactBar.Opacity = 1;
            Shell.Width = ExpandedWidth;
            Shell.Padding = new Thickness(10, 7, 10, 10);
            Expanded.Visibility = Visibility.Visible;
            Header.Opacity = Views.Opacity = 0;
            UpdateLayout();
            _island.Jump(CompactWidth, CompactH, CompactR);
            _island.Open(() => { _switching = false; _anchorTop = false; MinHeight = 0; });
            Render(_state.Current);
            // Inhalt erst, wenn die Insel fast offen ist: sonst schneidet ihre Kante die Karten
            Motion.Run(Header, OpacityProperty, 0, 1, 220, Co.OutE, 240);
            Motion.Run(Views, OpacityProperty, 0, 1, 220, Co.OutE, 260);
            Motion.Reveal(_bluetoothCardShown ? [BluetoothCard] : RevealParts(CurrentView), 280, 55);
        });
    }

    // Beenden: Inhalt blendet aus, der Avatar schließt die Augen, die Insel zieht sich zur kleinen Notch zusammen
    // und dann ganz in den oberen Rand zurück
    private void PlayClose(Action done)
    {
        ActiveMascot.Flash(Mood.Content, 3000);
        Motion.Run(_compact ? CompactBar : Expanded, OpacityProperty, 1, 0, 140, Co.OutE);
        void Retract() => _island.Close(NotchW, 0, CompactR, done, 300);
        if (_compact) Retract();
        else _island.Close(StartW, CompactH, CompactR, () => Motion.After(90, Retract));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _timer.Stop();                                                      // keine neue Aufzeichnung nach dem Export
        var hwnd = Native.Handle(this);
        foreach (var (action, _, _) in Hotkeys.All) Native.UnregisterHotKey(hwnd, (int)action);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        try { _ride.Export(); }
        catch (Exception ex) { RawLog.Note("Export on quit failed: " + ex.Message); }   // die CSV bleibt, Rettung beim nächsten Start
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
