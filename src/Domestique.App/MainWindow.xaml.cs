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
    private const double BaseWidth = 368;                     // 340 Panel + 2 × 14 Notch-Ohren
    private const double SnapDistance = 28, CenterSnap = 90;  // Andocken am oberen Rand, Einrasten in der Mitte
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
    private BleTrainer? _trainer;
    private BleHeartRate? _strap;
    private SettingsWindow? _settingsWindow;
    private Func<RoutePoint, Point>? _mapProject, _profileProject;
    private bool _clickThrough, _closing, _trainerLoop, _strapLoop, _connectingTrainer, _connectingStrap;
    private bool _wasConnected, _everConnected, _hadControl, _controlLost, _strapWas, _wasFinished, _dockCentered;
    private string _message = "Suche Trainer …";
    private string? _lastTrainerError, _lastStrapError;
    private long _lastTick;
    private int _ticks;

    public MainWindow()
    {
        InitializeComponent();
        _guard = new ModeGuard(command => _trainer?.Control?.SetTarget(command));
        _timer.Tick += (_, _) => OnTick();
        _toastTimer.Tick += (_, _) => NextToast();
        SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged || e.PreviousSize.Height <= 0) return;
            if (_settings.Docked) Dock(center: _dockCentered);     // angedockt: oben bleiben
            else Top -= e.NewSize.Height - e.PreviousSize.Height;   // sonst bleibt der untere Rand stehen
        };
    }

    public string TrainerStatus => _trainer is { } t && _state.Current.Connected ? $"{t.Name} · verbunden" : _message;
    public string StrapStatus => _state.Current.StrapConnected ? "Pulsgurt verbunden" : "kein Pulsgurt";

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

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RawLog.Note($"Domestique gestartet {DateTime.Now:yyyy-MM-dd}");
        ApplySettings();                                                   // Stil und Größe vor dem Positionieren
        if (_settings.Docked && _settings.NotchDock && _settings.Left is double dockLeft) { Left = dockLeft; Dock(center: IsCentered()); }
        else if (_settings.Left is double left && _settings.Top is double top && IsOnScreen(left, top)) { Left = left; Top = top; SetDocked(false); }
        else PlaceDefault();
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
                await Task.Delay(TimeSpan.FromSeconds(10));
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
            if (address is null) { _message = "Kein Trainer gefunden"; return false; }
            _message = "Verbinde …";
            await trainer.ConnectAsync(address.Value);
            _trainer = trainer;
            _lastTrainerError = null;
            _settings.TrainerAddress = address;
            _settings.Save();
            if (trainer.PowerRange is { } range) _guard.PowerLimits = range;
            _guard.Reapply();                                               // Startzustand flache Straße oder das schon gewählte Ziel
            _message = "Warte auf Trainer …";                               // erscheint nur, falls die Verbindung später abreißt
            return true;
        }
        catch (Exception ex)
        {
            await trainer.DisposeAsync();                                   // halbe Verbindung freigeben
            if (ex.Message != _lastTrainerError)
            {
                RawLog.Note("Trainer: " + ex.Message);                      // bei Dauerfehlern nicht alle 10 s
                if (ex.Message.Contains("Bluetooth")) ShowToast(Mood.Dizzy, "Bluetooth ist aus", "Schalte Bluetooth ein, ich suche weiter.", 6);
            }
            _lastTrainerError = ex.Message;
            _message = ex.Message.Contains("Bluetooth") ? "Bluetooth ist aus" : "Verbindung fehlgeschlagen";
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
        if (_sim.Route is not null)
            _guard.SetGrade(_limiter.Next(_sim.GradePercent * _settings.Difficulty, 1));   // im ERG ignoriert (Review #1)
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

        // Kopfzeile: Verbindungspunkt, immer sichtbar
        SetFill(LinkDot, !s.Connected ? "MutedBrush" : stale || controlLost ? "WarningBrush" : "PositiveBrush");
        LinkDot.ToolTip = TrainerStatus;

        if (HomeView.Visibility == Visibility.Visible) RenderHome(s, stale, controlLost, zone);
        else if (RouteView.Visibility == Visibility.Visible) RenderRoute(s);
        else RenderErg();
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
        SubtitleText.Text = !s.Connected ? _message
            : stale ? "keine Daten"
            : controlLost ? "Steuerung verloren"
            : _sim.Finished ? "Ziel erreicht"
            : _trainer?.Name ?? "Trainer";

        SetFill(TrainerDot, !s.Connected ? "MutedBrush" : stale ? "WarningBrush" : "PositiveBrush");
        SetFill(ControlDot, !s.Connected || _trainer?.Control is null ? "MutedBrush" : controlLost ? "NegativeBrush" : "PositiveBrush");
        SetFill(HeartDot, s.HeartRateBpm is null ? "MutedBrush" : "PositiveBrush");
        SetFill(RecordDot, _ride.Current is null ? "MutedBrush" : "NegativeBrush");

        PowerRun.Text = stale ? "–" : s.Power3sW.ToString();
        ZoneChip.Visibility = zone > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (zone > 0) { ZoneText.Text = $"Z{zone}"; ZoneChip.Background = ZoneBrushes[zone]; }
        TargetChip.Visibility = _guard.TargetPowerW is null ? Visibility.Collapsed : Visibility.Visible;
        TargetChipText.Text = $"ZIEL {_guard.TargetPowerW}";

        CadenceRow.Visibility = s.CadenceRpm is null ? Visibility.Collapsed : Visibility.Visible;   // D100 liefert keine
        CadenceValue.Text = $"{(stale ? 0 : s.CadenceRpm ?? 0):0} U/min";
        HeartRow.Visibility = s.HeartRateBpm is null ? Visibility.Collapsed : Visibility.Visible;
        HeartValue.Text = $"{s.HeartRateBpm} bpm";
        SpeedValue.Text = $"{s.VirtualSpeedKmh:0.0} km/h";
        DistanceValue.Text = $"{s.DistanceM / 1000:0.00} km";
        var time = _ride.Elapsed(Stopwatch.GetElapsedTime(AppStart));
        TimeRow.Visibility = time is null ? Visibility.Collapsed : Visibility.Visible;
        if (time is { } t) TimeValue.Text = $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private void RenderRoute(SessionSnapshot s)
    {
        bool loaded = _sim.Route is not null;
        RouteEmpty.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        RouteLoaded.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        if (_sim.Route is not { } r) return;
        double done = Math.Min(_sim.RouteDistanceM, r.LengthM);
        RouteNameText.Text = r.Name.ToUpperInvariant();
        RouteDistanceValue.Text = $"{done / 1000:0.0} / {r.LengthM / 1000:0.0} km";
        GradeValue.Text = $"{s.GradePercent:+0.0;-0.0;0.0} %";
        RouteSpeedValue.Text = $"{s.VirtualSpeedKmh:0.0} km/h";
        ProgressScale.ScaleX = done / r.LengthM;
    }

    private void RenderErg()
    {
        bool erg = _guard.Mode == ControlMode.Erg;
        ErgRun.Text = _guard.TargetPowerW?.ToString() ?? "–";
        ModeText.Text = erg ? "ERG AKTIV" : _guard.Mode == ControlMode.Sim ? "SIMULATION" : "FREI";
        if ((ModeChip.Tag as bool?) != erg)
        {
            ModeChip.Tag = erg;
            ModeChip.SetResourceReference(Border.BackgroundProperty, erg ? "AccentBrush" : "ChipBrush");
            ModeText.SetResourceReference(TextBlock.ForegroundProperty, erg ? "OnAccentBrush" : "TextBrush");
        }
        ErgToggleButton.Content = erg ? "ERG beenden" : "ERG starten";
        var (min, max) = _guard.PowerLimits;
        ErgHint.Text = erg ? $"Bereich {min}–{max} W · Strg+Alt+Bild↑/↓ ±10 W"
            : $"Simulation: {_limiter.Current:+0.0;-0.0;0.0} % am Trainer · Strg+Alt+Bild↑ startet ERG";
    }

    // Brush aus dem Stil setzen, der beim Stilwechsel mitwechselt. Nur bei Änderung, damit nichts neu gezeichnet wird.
    private static void SetFill(System.Windows.Shapes.Shape shape, string key)
    {
        if ((string?)shape.Tag == key) return;
        shape.Tag = key;
        shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
    }

    // ---------- Meldungen ----------

    private void DetectEvents(SessionSnapshot s, bool controlLost)
    {
        if (s.Connected != _wasConnected)
        {
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

    private void NextToast()
    {
        _toastTimer.Stop();
        if (!_toasts.TryDequeue(out var t)) { Toast.Visibility = Visibility.Collapsed; return; }
        ToastAvatar.Mood = t.Mood;
        ToastTitle.Text = t.Title;
        ToastText.Text = t.Text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Interval = TimeSpan.FromSeconds(t.Seconds);
        _toastTimer.Start();
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        NextToast();
    }

    // ---------- Ansichten ----------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomeView is null || RouteView is null || ErgView is null) return;   // während InitializeComponent
        HomeView.Visibility = HomeTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RouteView.Visibility = RouteTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ErgView.Visibility = ErgTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Render(_state.Current);
        MoveDots();
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

    private void LoadRoute_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX-Strecke (*.gpx)|*.gpx" };
        if (dialog.ShowDialog(this) == true) ImportAndLoad(dialog.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var file in files) ImportAndLoad(file);
    }

    private void ImportAndLoad(string path)
    {
        try { LoadFile(_library.Import(path)); }                           // landet automatisch in der Bibliothek
        catch (Exception ex) { ShowToast(Mood.Dizzy, "Datei nicht lesbar", ex.Message, 8); }
    }

    public void LoadFile(string path)
    {
        try
        {
            var route = GpxLoader.Load(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
            _sim.Load(route);
            MapLine.Points = RouteGeometry.Map(route, 86, 86, out _mapProject);
            ProfileLine.Points = RouteGeometry.Profile(route, 194, 86, out _profileProject);
            ProfileArea.Points = RouteGeometry.Area(ProfileLine.Points, 86);
            RouteTab.IsChecked = true;                                      // gleich zeigen, was geladen wurde
            Render(_state.Current);
            MoveDots();
            ShowToast(Mood.Happy, "Strecke geladen", $"{route.Name} · {route.LengthM / 1000:0.0} km");
            RawLog.Note($"Strecke geladen: {route.Name}, {route.LengthM / 1000:0.0} km");
        }
        catch (Exception ex) { ShowToast(Mood.Dizzy, "Strecke nicht lesbar", ex.Message, 8); }
    }

    private void MoveDots()
    {
        if (_sim.Route is not { } route || _mapProject is null || _profileProject is null || RouteView.Visibility != Visibility.Visible) return;
        var p = route.At(_sim.RouteDistanceM);
        var m = _mapProject(p);
        Canvas.SetLeft(MapDot, m.X - 4);
        Canvas.SetTop(MapDot, m.Y - 4);
        var q = _profileProject(p);
        Canvas.SetLeft(ProfileDot, q.X - 4);
        Canvas.SetTop(ProfileDot, q.Y - 4);
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

    private void OpenSettings()
    {
        if (_clickThrough) SetClickThrough(false);
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(this, _settings, _library);
        // neben dem Overlay aufklappen wie ein Popover: darunter, wenn oben Platz ist, sonst darüber
        var screen = Native.MonitorBounds(this);
        double left = Math.Clamp(Left + ActualWidth - _settingsWindow.Width - 14, screen.Left + 8, screen.Right - _settingsWindow.Width - 8);
        double below = Top + ActualHeight + 8, above = Top - _settingsWindow.Height - 8;
        _settingsWindow.Left = left;
        _settingsWindow.Top = below + _settingsWindow.Height <= screen.Bottom ? below : Math.Max(screen.Top + 8, above);
        _settingsWindow.Show();
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ---------- Position und Notch ----------

    // Ziehen und loslassen: nah am oberen Rand dockt das Overlay an, nah der Mitte rastet es zentriert ein.
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var before = new Point(Left, Top);
        DragMove();
        if (!_settings.NotchDock || before == new Point(Left, Top)) return;    // nur Klick, nicht verschoben
        var screen = Native.MonitorBounds(this);
        if (Top - screen.Top < SnapDistance) Dock(center: Math.Abs(Left + ActualWidth / 2 - screen.Left - screen.Width / 2) < CenterSnap);
        else SetDocked(false);
        _settings.Left = Left;
        _settings.Top = Top;
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
        Top = area.Bottom - ActualHeight - 16;
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

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

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
